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
using System.Net.Http;
using System.Text.Json;

namespace Scalae.Models
{
   public class ClientDetection
    {
        // Default credentials that callers can set to feed into detection APIs when explicit
        // username/password are not provided to the individual methods.
        public static string? DefaultRunAsUsername { get; set; } = null;
        public static string? DefaultRunAsPassword { get; set; } = null;

        // Agent preferences: server can set these so discovery prefers contacting the agent's HTTP /info endpoint.
        public static int AgentHttpPort { get; set; } = 37021;
        public static string? AgentToken { get; set; } = "ScalaeWorks";

        private readonly ILogger<ClientDetection> _logger;
        // Change the static readonly field to static (removable readonly) to allow assignment in SetStaticLogger
        private static ILogger<ClientDetection> _staticLogger = NullLogger<ClientDetection>.Instance;

        // Accept the logging abstraction via constructor injection.
        public ClientDetection(ILoggingService? loggingService = null)
        {
            _logger = loggingService?.CreateLogger<ClientDetection>() ?? NullLogger<ClientDetection>.Instance;
        }
        
        public static void SetStaticLogger(ILogger<ClientDetection> logger)
        {
            _staticLogger = logger;
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
        public static async Task<IEnumerable<ClientMachine>> ClientDetectAutoAsync(int timeoutMs = 3000, string? username = null, string? password = null)
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
                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
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
                    _staticLogger.LogInformation("Received UDP response from {IP}: {Response}", remoteEp.Address, resp);
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
                        isActive: true // <-- set to true here, since UDP response was received
);

                    // Use MAC if available as the unique key, otherwise IP
                    var key = NormalizeMac(cm.MACAddress) ?? (cm.IPAddress ?? Guid.NewGuid().ToString());
                    discovered.TryAdd(key, cm);

                    // Try to contact an agent HTTP /info endpoint on the responder to get authoritative info.
                    // This is non-blocking (short timeout) and will silently fall back to UDP-parsed values.
                    try
                    {
                        _staticLogger.LogInformation("Received UDP response from {IP}: {Response}", remoteEp.Address, resp);
                        var (agentName, agentMac, agentOs) = await TryGetAgentInfoAsync(ip);
                        _staticLogger.LogInformation("Agent HTTP /info result for {IP}: Name={Name}, MAC={MAC}, OS={OS}", ip, agentName, agentMac, agentOs);

                        // after agent probe returned values:
                        if (!string.IsNullOrWhiteSpace(agentName) || !string.IsNullOrWhiteSpace(agentMac) || !string.IsNullOrWhiteSpace(agentOs))
{
    // update local variables
    name = string.IsNullOrWhiteSpace(agentName) ? name : agentName;
    mac  = string.IsNullOrWhiteSpace(agentMac)  ? mac  : agentMac;
    os   = string.IsNullOrWhiteSpace(agentOs)   ? os   : agentOs;

    // Update the previously-added ClientMachine entry in 'discovered'
    var normalizedNewMac = NormalizeMac(mac);
    var currentKey = key;
    var newKey = normalizedNewMac ?? (ip ?? Guid.NewGuid().ToString());

    // Try to replace existing entry atomically
    if (discovered.TryGetValue(currentKey, out var existingCm))
    {
        existingCm.Name = name;
        existingCm.MACAddress = NormalizeMac(mac);
        existingCm.OperatingSystem = os;
        existingCm.IsActive = true;

        // If the unique key should be the MAC and it changed, rekey the dictionary
        if (!string.Equals(currentKey, newKey, StringComparison.OrdinalIgnoreCase))
        {
            // remove old key and add new key (best-effort)
            discovered.TryRemove(currentKey, out _);
            discovered.TryAdd(newKey, existingCm);
            key = newKey; // update local key variable if used later
        }
    }
}
                    }
                    catch (Exception ex)
                    {
                        _staticLogger.LogDebug(ex, "Agent /info probe failed for {IP}; continuing with UDP values", ip);
                    }
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
            // Skip NetServerEnum if we already discovered hosts via UDP/agent responses.
            if (discovered.Count == 0)
            {
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
            }
            else
            {
                return discovered.Values.OrderBy(d => d.IPAddress ?? string.Empty).ToArray();
            }

            return discovered.Values.OrderBy(d => d.IPAddress ?? string.Empty).ToArray();
        }

        private static (string? name, string? mac, string? os) TryGetAgentInfo(string ip)
        {
            try
            {
                using var client = new HttpClient() { Timeout = TimeSpan.FromMilliseconds(2000) };
                if (!string.IsNullOrWhiteSpace(AgentToken))
                {
                    if (!client.DefaultRequestHeaders.Contains("X-Scalae-Token"))
                        client.DefaultRequestHeaders.Add("X-Scalae-Token", AgentToken);
                }

                var uri = new UriBuilder("http", ip, AgentHttpPort, "/info").Uri;

                // Use async properly and wait synchronously in a way that avoids deadlocks
                var respTask = client.GetAsync(uri);
                respTask.Wait(); // Wait for completion

                var resp = respTask.Result;
                if (!resp.IsSuccessStatusCode) return (null, null, null);

                var jsonTask = resp.Content.ReadAsStringAsync();
                jsonTask.Wait();
                var json = jsonTask.Result;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? name = root.TryGetProperty("name", out var pName) ? pName.GetString() : null;
                string? mac = root.TryGetProperty("mac", out var pMac) ? pMac.GetString() : null;
                string? os = root.TryGetProperty("os", out var pOs) ? pOs.GetString() : null;

                return (name, mac, os);
            }
            catch (AggregateException aggEx) when (aggEx.InnerException is TaskCanceledException)
            {
                _staticLogger.LogDebug(aggEx.InnerException, "Agent /info probe timed out for {IP}", ip);
                return (null, null, null);
            }
            catch (Exception ex)
            {
                _staticLogger.LogDebug(ex, "Agent /info probe failed for {IP}", ip);
                return (null, null, null);
            }
        }

private static async Task<(string? name, string? mac, string? os)> TryGetAgentInfoAsync(string ip)
{
    try
    {
        _staticLogger.LogInformation("Entered TryGetAgentInfoAsync for {IP}", ip);
        using var client = new HttpClient() { Timeout = TimeSpan.FromMilliseconds(5000) };
        if (!string.IsNullOrWhiteSpace(AgentToken))
        {
            if (!client.DefaultRequestHeaders.Contains("X-Scalae-Token"))
                client.DefaultRequestHeaders.Add("X-Scalae-Token", AgentToken);
        }

        var uri = new UriBuilder("http", ip, AgentHttpPort, "/info").Uri;
        var resp = await client.GetAsync(uri);
        if (!resp.IsSuccessStatusCode) return (null, null, null);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? name = root.TryGetProperty("name", out var pName) ? pName.GetString() : null;
        string? mac = root.TryGetProperty("mac", out var pMac) ? pMac.GetString() : null;
        string? os = root.TryGetProperty("os", out var pOs) ? pOs.GetString() : null;

        return (name, mac, os);
    }
    catch (TaskCanceledException tcex)
    {
        _staticLogger.LogDebug(tcex, "Agent /info probe timed out for {IP}", ip);
        return (null, null, null);
    }
    catch (Exception ex)
    {
        _staticLogger.LogDebug(ex, "Agent /info probe failed for {IP}", ip);
        return (null, null, null);
    }
}

private static string? NormalizeMac(string? mac)
{
    if (string.IsNullOrWhiteSpace(mac))
        return null;

    // Remove any non-hex characters
    var hex = Regex.Replace(mac, "[^0-9A-Fa-f]", "");
    if (hex.Length != 12)
    {
        // If it can't be normalized to 12 hex chars, return trimmed original value
        return mac.Trim();
    }

    // Format as uppercase colon-separated MAC (AA:BB:CC:DD:EE:FF)
    var sb = new StringBuilder(17);
    for (int i = 0; i < 12; i += 2)
    {
        if (i > 0) sb.Append(':');
        sb.Append(hex.Substring(i, 2).ToUpperInvariant());
    }
    return sb.ToString();
}

private static List<string> GetWorkstationsViaNetServerEnum(string? username, string? password)
{
    var result = new List<string>();

    // Only attempt on Windows
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return result;

    IntPtr bufPtr = IntPtr.Zero;
    try
    {
        const int level = 100; // SERVER_INFO_100 (contains name)
        int entriesRead = 0;
        int totalEntries = 0;

        var status = NetServerEnum(
            servername: null,
            level: level,
            bufptr: out bufPtr,
            prefmaxlen: -1,
            entriesRead: out entriesRead,
            totalEntries: out totalEntries,
            servertype: SV_TYPE_WORKSTATION,
            domain: null,
            resume_handle: IntPtr.Zero);

        if (status == NERR_Success && entriesRead > 0 && bufPtr != IntPtr.Zero)
        {
            var structSize = Marshal.SizeOf(typeof(SERVER_INFO_100));
            for (int i = 0; i < entriesRead; i++)
            {
                var itemPtr = new IntPtr(bufPtr.ToInt64() + i * structSize);
                var si = Marshal.PtrToStructure<SERVER_INFO_100>(itemPtr);
                // Check the name field directly; the struct itself cannot be null
                if (!string.IsNullOrWhiteSpace(si.sv100_name))
                    result.Add(si.sv100_name);
            }
        }
    }
    catch (Exception ex)
    {
        _staticLogger.LogDebug(ex, "NetServerEnum failed");
    }
    finally
    {
        if (bufPtr != IntPtr.Zero)
            NetApiBufferFree(bufPtr);
    }

    return result;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
private struct SERVER_INFO_100
{
    public int sv100_platform_id;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string sv100_name;
}

[DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern int NetServerEnum(
    string? servername,
    int level,
    out IntPtr bufptr,
    int prefmaxlen,
    out int entriesRead,
    out int totalEntries,
    uint servertype,
    string? domain,
    IntPtr resume_handle);

[DllImport("Netapi32.dll", SetLastError = true)]
private static extern int NetApiBufferFree(IntPtr Buffer);

// Add near other private static helpers (e.g. NormalizeMac)
[DllImport("iphlpapi.dll", ExactSpelling = true, SetLastError = true)]
private static extern int SendARP(uint DestIP, uint SrcIP, byte[] pMacAddr, ref uint PhyAddrLen);

private static string? GetMacFromArp(string ip)
{
    if (string.IsNullOrWhiteSpace(ip))
        return null;

    // Try native SendARP on Windows
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var ipAddr))
                return null;

            var addrBytes = ipAddr.GetAddressBytes();
            // BitConverter expects little-endian ordering on little-endian machines;
            // SendARP commonly accepts the raw uint from the IPAddress bytes.
            uint dest = BitConverter.ToUInt32(addrBytes, 0);

            var macBuffer = new byte[6];
            uint macLen = (uint)macBuffer.Length;
            var r = SendARP(dest, 0, macBuffer, ref macLen);
            if (r == 0 && macLen > 0)
            {
                var macSb = new StringBuilder();
                for (int i = 0; i < macLen; i++)
                {
                    if (i > 0) macSb.Append(':');
                    macSb.Append(macBuffer[i].ToString("X2"));
                }
                return macSb.ToString();
            }
        }
        catch
        {
            // fall through to arp -a fallback
        }
    }

    // Fallback: parse `arp -a` output (cross-platform attempt)
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "arp" : "arp",
            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"-a {ip}" : "-a",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
        p?.WaitForExit(1000);

        // Look for the IP in output and extract MAC-like token
        // Windows arp line example: "  192.168.1.5           00-11-22-33-44-55     dynamic"
        // Unix example: "192.168.1.5 ether 00:11:22:33:44:55 C eth0"
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.Contains(ip)) continue;

            // Extract possible MAC token
            var parts = Regex.Split(line.Trim(), @"\s+");
            foreach (var part in parts)
            {
                // Accept formats AA-BB-CC-DD-EE-FF or AA:BB:CC:DD:EE:FF
                if (Regex.IsMatch(part, "^[0-9A-Fa-f]{2}([-:])[0-9A-Fa-f]{2}(\\1[0-9A-Fa-f]{2}){4}$"))
                {
                    // Normalize to colon-separated uppercase
                    var normalized = Regex.Replace(part, "[-:]", ":").ToUpperInvariant();
                    return normalized;
                }
            }
        }
    }
    catch
    {
        // ignore fallback failures
    }

    return null;
}

private static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
{
    if (address == null) throw new ArgumentNullException(nameof(address));
    if (mask == null) throw new ArgumentNullException(nameof(mask));
    if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
        throw new ArgumentException("Only IPv4 addresses are supported by ComputeBroadcast.");

    var addrBytes = address.GetAddressBytes();
    var maskBytes = mask.GetAddressBytes();
    if (addrBytes.Length != maskBytes.Length)
        throw new ArgumentException("Address and mask lengths differ.");

    var broadcast = new byte[addrBytes.Length];
    for (int i = 0; i < addrBytes.Length; i++)
    {
        // (~maskBytes[i] & 0xFF) to avoid sign-extension when converting to byte
        broadcast[i] = (byte)(addrBytes[i] | (~maskBytes[i] & 0xFF));
    }

    return new IPAddress(broadcast);
}

private static Dictionary<string, string> GetArpTable()
{
    var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "arp",
            Arguments = "-a",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
        p?.WaitForExit(1000);

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // Find IPv4 and MAC tokens in the line
            var ipMatch = Regex.Match(line, @"\b(?:\d{1,3}\.){3}\d{1,3}\b");
            var macMatch = Regex.Match(line, @"[0-9A-Fa-f]{2}([-:])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}");

            if (ipMatch.Success && macMatch.Success)
            {
                var ip = ipMatch.Value;
                var mac = NormalizeMac(macMatch.Value);
                if (!string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(mac))
                {
                    if (!mapping.ContainsKey(ip))
                        mapping[ip] = mac!;
                }
            }
        }
    }
    catch
    {
        // Swallow errors and return whatever we could parse (consistent with other helpers)
    }

    return mapping;
}
    }
}