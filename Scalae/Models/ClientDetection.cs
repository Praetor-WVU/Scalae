using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scalae.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Principal;
using System.Security.Permissions;
using Microsoft.Win32.SafeHandles;
using System.Runtime.ConstrainedExecution;
using System.Security;

namespace Scalae.Models
{
   public class ClientDetection
    {
        // Default credentials that callers can set to feed into detection APIs when explicit
        // username/password are not provided to the individual methods.
        public static string? DefaultRunAsUsername { get; set; } = null;
        public static string? DefaultRunAsPassword { get; set; } = null;

        private readonly ILogger<ClientDetection> _logger;
        // Add a static logger for use inside static methods
        private static readonly ILogger<ClientDetection> _staticLogger = NullLogger<ClientDetection>.Instance;

        // Accept the logging abstraction via constructor injection.
        public ClientDetection(ILoggingService? loggingService = null)
        {
            _logger = loggingService?.CreateLogger<ClientDetection>() ?? NullLogger<ClientDetection>.Instance;
        }

        private const int DiscoveryPort = 37020;
        private const string DiscoveryRequest = "SCALAE_DISCOVER_v1";
        private const string DiscoveryResponsePrefix = "SCALAE_RESPONSE_v1|";

        // NetServerEnum constants for workstation enumeration
        private const uint SV_TYPE_WORKSTATION = 0x00000001;
        private const int NERR_Success = 0;

        // LogonUser constants for impersonation
        private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
        private const int LOGON32_PROVIDER_WINNT50 = 3;

        // Public simple APIs
        public static ClientMachine ClientDetectIP(string ipAddress, string? username = null, string? password = null, int timeoutMs = 1000)
        {
            // Use configured defaults when explicit credentials are not supplied
            username ??= DefaultRunAsUsername;
            password ??= DefaultRunAsPassword;

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                _staticLogger.LogError("IP address is required for IP-based detection");
                throw new ArgumentNullException(nameof(ipAddress));
            }

            // Normalize IP
            IPAddress? ip = null;
            if (!IPAddress.TryParse(ipAddress, out ip))
            {
                // Try DNS resolve
                try
                {
                    var entry = Dns.GetHostEntry(ipAddress);
                    ip = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch
                {
                    _staticLogger.LogWarning("Failed to parse IP address {IP} or resolve DNS; proceeding with raw input", ipAddress);
                    // leave null
                }
            }

            var isAlive = false;
            string? hostName = null;
            string? os = null;
            string? mac = null;

            // Ping to check reachability
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(ipAddress, timeoutMs);
                isAlive = reply != null && reply.Status == IPStatus.Success;
            }
            catch
            {
                _staticLogger.LogWarning("Failed to ping IP {IP} during detection", ipAddress);
                isAlive = false;
            }

            // Try DNS name lookup
            try
            {
                if (ip != null)
                {
                    hostName = Dns.GetHostEntry(ip).HostName;
                }
            }
            catch
            {
                _staticLogger.LogWarning("Failed to perform reverse DNS lookup for IP {IP}", ipAddress);
                hostName ??= null;
            }

            // Try WMI for detailed info (works on Windows, may require credentials & remote admin enabled)
            try
            {
                var scopePath = $"\\\\{ipAddress}\\root\\cimv2";
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                };

                // If username/password are provided they should be in the form:
                //   domain\\username OR machineName\\username
                // and the password for that account. The account must have remote admin privileges
                // on the target host (local Administrators or equivalent domain account) for WMI queries
                // to succeed. If the target is in a domain, a domain account with appropriate rights
                // should be used. Note: WMI over DCOM uses the credentials provided in ConnectionOptions;
                // these are the only credentials that will be used for the remote WMI connection.
                if (!string.IsNullOrWhiteSpace(username))
                {
                    options.Username = username;
                    options.Password = password;
                }

                var scope = new ManagementScope(scopePath, options);
                scope.Connect();

                // OS info
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Caption, Version FROM Win32_OperatingSystem")))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        os = $"{mo["Caption"]} ({mo["Version"]})";
                        break;
                    }
                }

                // Computer name
                if (string.IsNullOrEmpty(hostName))
                {
                    using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Win32_ComputerSystem")))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            hostName = mo["Name"]?.ToString();
                            break;
                        }
                    }
                }

                // MAC address - find first IP-enabled NIC
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT MACAddress, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True")))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var macObj = mo["MACAddress"];
                        if (macObj != null)
                        {
                            mac = macObj.ToString();
                            break;
                        }
                    }
                }
            }
            catch
            {
                _staticLogger.LogWarning("WMI query failed for IP {IP}; this may be expected if the target is not Windows or remote WMI is not enabled", ipAddress);
                // WMI failed -- fallback to arp lookup and local DNS
                try
                {
                    mac ??= GetMacFromArp(ipAddress);
                }
                catch 
                { 
                    _staticLogger.LogWarning("Failed to get MAC address from ARP table for IP {IP}", ipAddress);
                    /* ignore */
                }
            }

            // Final best-effort hostname
            try
            {
                if (string.IsNullOrEmpty(hostName))
                {
                    hostName = Dns.GetHostEntry(ipAddress).HostName;
                }
            }
            catch
            {
                _staticLogger.LogWarning("Final attempt at reverse DNS lookup failed for IP {IP}", ipAddress);
                // ignore
            }

            _staticLogger.LogInformation("Completed IP-based detection for {IP}: HostName={HostName}, MAC={MAC}, OS={OS}, IsAlive={IsAlive}", ipAddress, hostName, mac, os, isAlive);
            return new ClientMachine(
                name: hostName,
                macAddress: NormalizeMac(mac),
                ipAddress: ipAddress,
                operatingSystem: os,
                isActive: isAlive
            );
        }

        public static ClientMachine ClientDetectMAC(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress))
                throw new ArgumentNullException(nameof(macAddress));

            var target = NormalizeMac(macAddress);
            var mapping = GetArpTable();

            // Try to find matching IP by MAC
            foreach (var kv in mapping)
            {
                if (NormalizeMac(kv.Value) == target)
                {
                    // Found IP -> reuse IP detection
                    _staticLogger.LogDebug("Found IP {IP} for MAC {MAC} in ARP table, performing IP-based detection", kv.Key, target);
                    return ClientDetectIP(kv.Key);
                }
            }

            // Not found in ARP table -> return object with only MAC
            _staticLogger.LogWarning("MAC address {MAC} not found in ARP table; returning minimal ClientMachine entry", target);
            return new ClientMachine(
                name: null,
                macAddress: target,
                ipAddress: null,
                operatingSystem: null,
                isActive: false
            );
        }

        // Broadcast-based discovery: send UDP broadcasts and gather responses from clients/agents that implement the discovery protocol.
        // Returns zero or more discovered ClientMachine entries.
        public static IEnumerable<ClientMachine> ClientDetectAuto(int timeoutMs = 3000, string? username = null, string? password = null)
        {
            var discovered = new ConcurrentDictionary<string, ClientMachine>(StringComparer.OrdinalIgnoreCase);

            // Prepare request bytes
            var requestBytes = Encoding.UTF8.GetBytes(DiscoveryRequest);

            // Bind the listener to the discovery port so replies addressed to port 37020 are received.
            // Create a socket and enable address reuse before binding to avoid races on some platforms.
            Socket? listenerSocket = null;
            var candidates = new List<IPAddress> { IPAddress.Any };
            try
            {
                candidates.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses
                        .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(u => u.Address)));
            }
            catch { /* ignore enumeration failures */ }

            foreach (var addr in candidates.Distinct())
            {
                Socket? s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    s.Bind(new IPEndPoint(addr, DiscoveryPort));
                    listenerSocket = s;
                    break;
                }
                catch (Exception ex)
                {
                    try { Debug.WriteLine($"Bind to {addr}:{DiscoveryPort} failed: {ex.Message}"); } catch { }
                    _staticLogger.LogDebug(ex, "Bind to {Addr}:{Port} failed", addr, DiscoveryPort);
                    s?.Dispose();
                }
            }

            if (listenerSocket == null)
            {
                try { Debug.WriteLine($"Falling back to ephemeral port (no bind to {DiscoveryPort} succeeded)"); } catch { }
                _staticLogger.LogWarning("Failed to bind discovery port {Port} on all interfaces; falling back to ephemeral port.", DiscoveryPort);
            }

            UdpClient udpClient;
            if (listenerSocket != null)
            {
                udpClient = new UdpClient();
                udpClient.Client = listenerSocket; // attach existing bound socket
            }
            else
            {
                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            }
            using var udp = udpClient;
            udp.EnableBroadcast = true;

            // Diagnostic: log which local endpoint we're listening on and whether we successfully bound to DiscoveryPort
            try
            {
                _staticLogger.LogInformation("Discovery listener local endpoint: {EP}; boundToDiscoveryPort={Bound}", udp.Client.LocalEndPoint, listenerSocket != null);
                try { Debug.WriteLine($"Discovery listener local endpoint: {udp.Client.LocalEndPoint}; boundToDiscoveryPort={listenerSocket != null}"); } catch { }
            }
            catch (Exception ex)
            {
                // Ensure logging doesn't interfere with discovery; swallow any logging exceptions
                _staticLogger.LogDebug(ex, "Failed to log discovery listener endpoint");
                try { Debug.WriteLine($"Failed to log discovery listener endpoint: {ex}"); } catch { }
            }

            // Send to global broadcast
            try
            {
                udp.Send(requestBytes, requestBytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch
            {
                _staticLogger.LogWarning("Failed to send discovery broadcast on global broadcast address");
                // ignore send errors for broadcast
            }

            // Send to per-interface broadcast addresses to increase reachability on multi-homed hosts
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                             .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                         n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var props = ni.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        var mask = ua.IPv4Mask;
                        if (mask == null) continue;
                        var broadcast = ComputeBroadcast(ua.Address, mask);
                        try
                        {
                            udp.Send(requestBytes, requestBytes.Length, new IPEndPoint(broadcast, DiscoveryPort));
                        }
                        catch
                        {
                            // ignore per-interface send errors
                        }
                    }
                }
            }
            catch
            {
                _staticLogger.LogWarning("Failed to enumerate network interfaces for per-interface broadcasts");
                // ignore enumeration errors
            }

            // Listen for responses until timeout
            var sw = Stopwatch.StartNew();
            // Use small receive timeout and loop until overall timeout to keep responsive to multiple responses
            udp.Client.ReceiveTimeout = 500;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    var data = udp.Receive(ref remoteEp); // will throw SocketException on timeout
                    if (data == null || data.Length == 0) continue;

                    var resp = Encoding.UTF8.GetString(data).Trim();
                    string? name = null;
                    string? mac = null;
                    string? os = null;
                    string ip = remoteEp.Address.ToString();

                    if (resp.StartsWith(DiscoveryResponsePrefix, StringComparison.Ordinal))
                    {
                        // Expected format: SCALAE_RESPONSE_v1|Name|MAC|OS
                        var parts = resp.Substring(DiscoveryResponsePrefix.Length).Split('|');
                        if (parts.Length >= 1) name = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
                        if (parts.Length >= 2) mac = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
                        if (parts.Length >= 3) os = string.IsNullOrWhiteSpace(parts[2]) ? null : parts[2];
                    }
                    else
                    {
                        // Unknown response; keep raw as name if small
                        if (resp.Length <= 64) name = resp;
                    }

                    // If responder echoed back the discovery request (or provided no useful name), try reverse DNS
                    if (string.IsNullOrWhiteSpace(name) || name.Equals(DiscoveryRequest, StringComparison.Ordinal))
                    {
                        try
                        {
                            var entry = Dns.GetHostEntry(ip);
                            if (!string.IsNullOrWhiteSpace(entry?.HostName))
                                name = entry.HostName;
                        }
                        catch
                        {
                            // ignore DNS failures; keep existing name (may be null or the raw response)
                        }
                    }

                    // If MAC not provided, try ARP table
                    if (string.IsNullOrWhiteSpace(mac))
                    {
                        try { mac = GetMacFromArp(ip); } catch { mac = null; }
                    }

                    var cm = new ClientMachine(
                        name: name,
                        macAddress: NormalizeMac(mac),
                        ipAddress: ip,
                        operatingSystem: os,
                        isActive: true
                    );

                    // Use MAC if available as the unique key, otherwise IP
                    var key = NormalizeMac(cm.MACAddress) ?? (cm.IPAddress ?? Guid.NewGuid().ToString());
                    discovered.TryAdd(key, cm);
                }
                catch (SocketException sex)
                {
                    // timeout -> loop again until overall timeout
                    if (sex.SocketErrorCode == SocketError.TimedOut)
                        continue;
                    // other socket errors -> break
                    break;
                }
                catch
                {
                    _staticLogger.LogWarning("Failed to process a discovery response: {Message}", "Malformed response or processing error");
                    // ignore malformed responses and continue
                }
            }

            // On Windows, augment UDP/broadcast discovery with NetServerEnum to ensure we only
            // collect workstation-class machines (desktops/laptops). NetServerEnum does not
            // accept credentials directly; if domain enumeration requires specific permissions
            // the process must be run under an account that has the rights to enumerate the
            // domain (for example a domain account). See comments in "GetWorkstationsViaNetServerEnum"
            // for more details.
            try
            {
                var workstationNames = GetWorkstationsViaNetServerEnum(username, password);
                if (workstationNames.Count > 0)
                {
                    // Filter discovered entries to only include those that match workstation names
                    var filtered = discovered.Values.Where(cm =>
                    {
                        if (!string.IsNullOrWhiteSpace(cm.Name) && workstationNames.Contains(cm.Name, StringComparer.OrdinalIgnoreCase))
                            return true;
                        // also try resolve IP to hostname
                        if (!string.IsNullOrWhiteSpace(cm.IPAddress))
                        {
                            try
                            {
                                var he = Dns.GetHostEntry(cm.IPAddress);
                                if (!string.IsNullOrWhiteSpace(he?.HostName) && workstationNames.Contains(he.HostName, StringComparer.OrdinalIgnoreCase))
                                    return true;
                            }
                            catch { }
                        }
                        return false;
                    }).ToList();

                    // Add any workstation names not seen via UDP by attempting a DNS resolve + IP check
                    foreach (var name in workstationNames)
                    {
                        if (filtered.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        try
                        {
                            var entry = Dns.GetHostEntry(name);
                            var addr = entry?.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                            if (addr != null)
                            {
                                // Pass through credentials so WMI queries inside ClientDetectIP can use them
                                var cm = ClientDetectIP(addr.ToString(), username, password);
                                filtered.Add(cm);
                            }
                        }
                        catch { }
                    }

                    return filtered.OrderBy(d => d.IPAddress ?? string.Empty).ToArray();
                }
            }
            catch (Exception ex)
            {
                _staticLogger.LogDebug(ex, "NetServerEnum workstation augmentation failed; returning UDP-discovered entries");
            }

            return discovered.Values.OrderBy(d => d.IPAddress ?? string.Empty).ToArray();
        }

        // Use NetServerEnum to get a list of workstation names on the current domain/segment.
        // This method returns host names for servers that have the SV_TYPE_WORKSTATION bit set.
        // Note about credentials: NetServerEnum does not accept explicit username/password parameters.
        // If enumeration of a domain requires credentials, run this process under an account
        // that has permission to enumerate the domain (for example, a domain user). If you need
        // to use explicit alternate credentials you must perform impersonation prior to calling
        // this function or use other APIs that accept credentials.
        private static HashSet<string> GetWorkstationsViaNetServerEnum(string? runAsUsername = null, string? runAsPassword = null)
        {
            // Prefer explicit params but fall back to configured defaults
            runAsUsername = string.IsNullOrWhiteSpace(runAsUsername) ? DefaultRunAsUsername : runAsUsername;
            runAsPassword = runAsPassword ?? DefaultRunAsPassword;

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IntPtr token = IntPtr.Zero;
            SafeAccessTokenHandle? safeHandle = null;
            try
            {
                // If credentials provided, attempt LogonUser and impersonation
                if (!string.IsNullOrWhiteSpace(runAsUsername) && runAsPassword != null)
                {
                    var domain = ".";
                    var user = runAsUsername;
                    var idx = runAsUsername.IndexOf('\\');
                    if (idx > 0)
                    {
                        domain = runAsUsername.Substring(0, idx);
                        user = runAsUsername.Substring(idx + 1);
                    }

                    var ok = LogonUser(user, domain, runAsPassword, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out token);
                    if (!ok)
                    {
                        var err = Marshal.GetLastWin32Error();
                        _staticLogger.LogWarning("LogonUser failed with error {Err} for user {User}; continuing without impersonation", err, runAsUsername);
                    }
                    else
                    {
                        try
                        {
                            // Transfer ownership of the raw token into a SafeAccessTokenHandle to ensure safe cleanup.
                            safeHandle = new SafeAccessTokenHandle(token);
                            // Prevent double-close: mark the raw token as transferred
                            token = IntPtr.Zero;
                        }
                        catch (Exception ex)
                        {
                            _staticLogger.LogWarning(ex, "Failed to create SafeAccessTokenHandle for user {User}; continuing without impersonation", runAsUsername);
                            try { if (token != IntPtr.Zero) CloseHandle(token); } catch { }
                            token = IntPtr.Zero;
                            safeHandle = null;
                        }
                    }
                }

                // NetServerEnum signature (level 101) via P/Invoke
                IntPtr bufPtr = IntPtr.Zero;
                try
                {
                    int entriesRead = 0;
                    int totalEntries = 0;

                    // Wrap NetServerEnum call in impersonation if we have a safe handle
                    Action doEnum = () =>
                    {
                        // servername = null for local computer domain; domain can be provided if desired
                        int rc = NetServerEnum(null, 101, out bufPtr, -1, out entriesRead, out totalEntries, SV_TYPE_WORKSTATION, null, IntPtr.Zero);
                        if (rc != NERR_Success || bufPtr == IntPtr.Zero || entriesRead == 0)
                        {
                            return;
                        }

                        var structSize = Marshal.SizeOf(typeof(SERVER_INFO_101));
                        var current = bufPtr;
                        for (int i = 0; i < entriesRead; i++)
                        {
                            var si = (SERVER_INFO_101)Marshal.PtrToStructure(current, typeof(SERVER_INFO_101))!;
                            if (!string.IsNullOrWhiteSpace(si.sv101_name))
                            {
                                // Only add if the type includes workstation bit
                                if ((si.sv101_type & SV_TYPE_WORKSTATION) == SV_TYPE_WORKSTATION)
                                    result.Add(si.sv101_name);
                            }
                            current = IntPtr.Add(current, structSize);
                        }
                    };

                    if (safeHandle != null)
                    {
                        WindowsIdentity.RunImpersonated(safeHandle, doEnum);
                    }
                    else
                    {
                        doEnum();
                    }
                }
                finally
                {
                    if (bufPtr != IntPtr.Zero)
                        NetApiBufferFree(bufPtr);
                }
            }
            finally
            {
                // Ensure safe handle is disposed and native token closed if not transferred.
                try
                {
                    safeHandle?.Dispose();
                }
                catch { }

                if (token != IntPtr.Zero)
                {
                    try { CloseHandle(token); } catch { }
                }
            }

             _staticLogger.LogInformation("NetServerEnum returned {Count} workstation entries", result.Count);
             return result;
         }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LogonUser(
            string lpszUsername,
            string lpszDomain,
            string lpszPassword,
            int dwLogonType,
            int dwLogonProvider,
            out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("Netapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NetServerEnum(
             [MarshalAs(UnmanagedType.LPWStr)] string servername,
             int level,
             out IntPtr bufptr,
             int prefmaxlen,
             out int entriesread,
             out int totalentries,
             uint servertype,
             [MarshalAs(UnmanagedType.LPWStr)] string domain,
             IntPtr resume_handle);

        [DllImport("Netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr Buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVER_INFO_101
        {
            public uint sv101_platform_id;
            [MarshalAs(UnmanagedType.LPWStr)] public string sv101_name;
            public uint sv101_version_major;
            public uint sv101_version_minor;
            public uint sv101_type;
            [MarshalAs(UnmanagedType.LPWStr)] public string sv101_comment;
        }

        // Additional helper that performs a parallel /24 sweep and returns discovered machines.
        public static IEnumerable<ClientMachine> DetectSubnet(string baseIp, int prefix = 24, int timeoutMs = 300)
        {
            if (string.IsNullOrWhiteSpace(baseIp))
            {
                _staticLogger.LogError("Base IP is required for subnet detection");
                throw new ArgumentNullException(nameof(baseIp));
            }

            // Only simple /24 supported for now
            var baseParts = baseIp.Split('.');
            if (baseParts.Length != 4)
                throw new ArgumentException("Base IP must be IPv4 like 192.168.1.0", nameof(baseIp));

            var baseNetwork = $"{baseParts[0]}.{baseParts[1]}.{baseParts[2]}";

            var results = new ConcurrentBag<ClientMachine>();
            Parallel.For(1, 255, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, i =>
            {
                var ip = $"{baseNetwork}.{i}";
                try
                {
                    using var ping = new Ping();
                    var reply = ping.Send(ip, timeoutMs);
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        var cm = ClientDetectIP(ip);
                        results.Add(cm);
                    }
                }
                catch
                {
                    _staticLogger.LogWarning("Failed to ping IP {IP} during subnet detection", ip);
                    // ignore host
                }
            });

            return results.OrderBy(r => r.IPAddress ?? string.Empty).ToArray();
        }

        // --- Internal helpers ---

        private static string? NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
            {
                _staticLogger.LogDebug("No MAC address provided to normalize");
                return null; 
            }

            var cleaned = Regex.Replace(mac, @"[^0-9A-Fa-f]", "");
            if (cleaned.Length != 12)
            {
                _staticLogger.LogWarning("Invalid MAC address format: {MAC}", mac);
                return mac.ToUpperInvariant();
            }
               
            var parts = Enumerable.Range(0, 6).Select(i => cleaned.Substring(i * 2, 2));
            _staticLogger.LogDebug("Normalized MAC address {Original} to {Normalized}", mac, string.Join(":", parts).ToUpperInvariant());
            return string.Join(":", parts).ToUpperInvariant();
        }

        private static string GetMacFromArp(string ipAddress)
        {
            var table = GetArpTable();
            if (table.TryGetValue(ipAddress, out var mac))
            {
                _staticLogger.LogDebug("Found MAC {MAC} for IP {IP} in ARP table", mac, ipAddress);
                return mac; 
            }

            throw new InvalidOperationException("MAC not found in ARP table for IP: " + ipAddress);
        }

        private static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
        {
            var addrBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var broad = new byte[addrBytes.Length];

            for (int i = 0; i < addrBytes.Length; i++)
            {
                broad[i] = (byte)(addrBytes[i] | (~maskBytes[i]));
            }

            _staticLogger.LogDebug("Computed broadcast address {Broadcast} from IP {IP} and Mask {Mask}", new IPAddress(broad), address, mask);
            return new IPAddress(broad);
        }

        // Returns mapping IP -> MAC from local ARP table by parsing 'arp -a' output.
        private static Dictionary<string, string> GetArpTable()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "arp",
                    Arguments = "-a",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                p?.WaitForExit(1000);

                // Parse lines like: "  192.168.1.1           00-11-22-33-44-55     dynamic"
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var rx = new Regex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9A-Fa-f:-]{17}|[0-9A-Fa-f-]{14,17}|[0-9A-Fa-f]{12})\s+\w+", RegexOptions.Compiled);

                foreach (var line in lines)
                {
                    var m = rx.Match(line);
                    if (m.Success)
                    {
                        var ip = m.Groups[1].Value.Trim();
                        var mac = m.Groups[2].Value.Trim().Replace('-', ':').ToUpperInvariant();
                        result[ip] = mac;
                    }
                }
            }
            catch
            {
                // ignore failures; return whatever we parsed (possibly empty)
                _staticLogger.LogError("Failed to retrieve ARP table: {Message}", "ARP command failed");
            }

            _staticLogger.LogInformation("Retrieved ARP table with {Count} entries", result.Count);
            return result;
        }
    }

}