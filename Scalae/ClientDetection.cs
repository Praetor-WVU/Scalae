using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Scalae
{
    public static class ClientDetection
    {
        private const int DiscoveryPort = 37020;
        private const string DiscoveryRequest = "SCALAE_DISCOVER_v1";
        private const string DiscoveryResponsePrefix = "SCALAE_RESPONSE_v1|";

        // Public simple APIs
        public static ClientMachine ClientDetectIP(string ipAddress, string? username = null, string? password = null, int timeoutMs = 1000)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                throw new ArgumentNullException(nameof(ipAddress));

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
                // WMI failed -- fallback to arp lookup and local DNS
                try
                {
                    mac ??= GetMacFromArp(ipAddress);
                }
                catch { /* ignore */ }
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
                // ignore
            }

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
                    return ClientDetectIP(kv.Key);
                }
            }

            // Not found in ARP table -> return object with only MAC
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
        public static IEnumerable<ClientMachine> ClientDetectAuto(int timeoutMs = 3000)
        {
            var discovered = new ConcurrentDictionary<string, ClientMachine>(StringComparer.OrdinalIgnoreCase);

            // Prepare request bytes
            var requestBytes = Encoding.UTF8.GetBytes(DiscoveryRequest);

            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.EnableBroadcast = true;

            // Send to global broadcast
            try
            {
                udp.Send(requestBytes, requestBytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch
            {
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
                    // ignore malformed responses and continue
                }
            }

            return discovered.Values.OrderBy(d => d.IPAddress ?? string.Empty).ToArray();
        }

        // Additional helper that performs a parallel /24 sweep and returns discovered machines.
        public static IEnumerable<ClientMachine> DetectSubnet(string baseIp, int prefix = 24, int timeoutMs = 300)
        {
            if (string.IsNullOrWhiteSpace(baseIp))
                throw new ArgumentNullException(nameof(baseIp));

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
                    // ignore host
                }
            });

            return results.OrderBy(r => r.IPAddress ?? string.Empty).ToArray();
        }

        // --- Internal helpers ---

        private static string? NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
                return null;

            var cleaned = Regex.Replace(mac, @"[^0-9A-Fa-f]", "");
            if (cleaned.Length != 12)
                return mac.ToUpperInvariant();

            var parts = Enumerable.Range(0, 6).Select(i => cleaned.Substring(i * 2, 2));
            return string.Join(":", parts).ToUpperInvariant();
        }

        private static string GetMacFromArp(string ipAddress)
        {
            var table = GetArpTable();
            if (table.TryGetValue(ipAddress, out var mac))
                return mac;

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
            }

            return result;
        }
    }
}
