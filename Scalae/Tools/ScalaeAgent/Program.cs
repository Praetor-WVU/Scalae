using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;
using System.Linq;

internal class Program
{
    private const int DefaultDiscoveryPort = 37020;
    private const int DefaultHttpPort = 37021;
    private const string DiscoveryRequest = "SCALAE_DISCOVER_v1";
    private const string DiscoveryResponsePrefix = "SCALAE_RESPONSE_v1|";

    static async Task<int> Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Stopping...");
        };

        // parse args
        int udpPort = DefaultDiscoveryPort;
        int httpPort = DefaultHttpPort;
        string token = Environment.GetEnvironmentVariable("SCALAE_AGENT_TOKEN") ?? string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            if (int.TryParse(args[i], out var p) && i == 0) udpPort = p;
            if (args[i].Equals("--http-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[i + 1], out httpPort);
                i++;
            }
            else if (args[i].Equals("--token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                token = args[i + 1];
                i++;
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Warning: no token provided. Metrics endpoint will reject requests without a token header.");
        }

        Console.WriteLine($"Scalae Agent starting. UDP {udpPort}, HTTP {httpPort}. Ctrl+C to stop.");
        var mac = GetPrimaryMac() ?? "UNKNOWN";
        var os = RuntimeInformation.OSDescription?.Trim() ?? Environment.OSVersion.ToString();
        var name = Environment.MachineName;

        // start HTTP server
        var httpTask = Task.Run(() => RunHttpServerAsync(httpPort, token, name, mac, os, cts.Token), cts.Token);

        // start UDP discovery responder
        var udpTask = Task.Run(() => RunUdpResponderAsync(udpPort, name, mac, os, cts.Token), cts.Token);

        await Task.WhenAll(httpTask, udpTask);

        Console.WriteLine("Agent stopped.");
        return 0;
    }

    private static async Task RunUdpResponderAsync(int localPort, string name, string mac, string os, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var receive = await udp.ReceiveAsync().WaitAsync(ct);
                    var msg = Encoding.UTF8.GetString(receive.Buffer).Trim();
                    var remoteEP = receive.RemoteEndPoint;
                    Console.WriteLine($"[UDP] Recv from {remoteEP.Address}:{remoteEP.Port}: '{msg}'");

                    if (string.Equals(msg, DiscoveryRequest, StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = $"{DiscoveryResponsePrefix}{name}|{mac}|{os}";
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        await udp.SendAsync(bytes, bytes.Length, remoteEP);
                        Console.WriteLine($"[UDP] Responded to {remoteEP.Address}:{remoteEP.Port} -> {payload}");
                    }
                    else
                    {
                        if (msg.Length <= 64)
                        {
                            var payload = $"{DiscoveryResponsePrefix}{msg}|{mac}|{os}";
                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await udp.SendAsync(bytes, bytes.Length, remoteEP);
                            Console.WriteLine($"[UDP] Echo-responded to {remoteEP.Address}:{remoteEP.Port}");
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UDP] Error: {ex.Message}");
                    await Task.Delay(500, ct).ContinueWith(_ => { });
                }
            }
        }
        finally
        {
            udp.Close();
        }
    }

    private static async Task RunHttpServerAsync(int httpPort, string token, string name, string mac, string os, CancellationToken ct)
    {
        var prefix = $"http://+:{httpPort}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"Failed to start HTTP listener on {prefix}. (Run as Admin or reserve the URL). Exception: {ex.Message}");
            return;
        }

        Console.WriteLine($"[HTTP] Listening on {prefix}");

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context = null;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP] Accept error: {ex.Message}");
                await Task.Delay(200, ct).ContinueWith(_ => { });
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var req = context.Request;
                    var res = context.Response;

                    // auth: require X-Scalae-Token header or Authorization: Bearer <token>
                    bool authorized = false;
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        var header = req.Headers["X-Scalae-Token"];
                        if (!string.IsNullOrWhiteSpace(header) && header == token) authorized = true;
                        else
                        {
                            var auth = req.Headers["Authorization"];
                            if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            {
                                var supplied = auth.Substring("Bearer ".Length).Trim();
                                if (supplied == token) authorized = true;
                            }
                        }
                    }
                    else
                    {
                        // token not configured: reject requests (avoid accidental exposure)
                        authorized = false;
                    }

                    if (!authorized)
                    {
                        res.StatusCode = (int)HttpStatusCode.Unauthorized;
                        var bytes = Encoding.UTF8.GetBytes("Unauthorized");
                        res.OutputStream.Write(bytes, 0, bytes.Length);
                        res.Close();
                        Console.WriteLine($"[HTTP] Unauthorized request from {req.RemoteEndPoint}");
                        return;
                    }

                    if (req.HttpMethod == "GET" && req.Url.AbsolutePath.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
                    {
                        var metrics = CollectMetrics(name, mac, os);
                        var json = JsonSerializer.Serialize(metrics);
                        var bytes = Encoding.UTF8.GetBytes(json);
                        res.ContentType = "application/json";
                        res.StatusCode = (int)HttpStatusCode.OK;
                        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        res.Close();
                        Console.WriteLine($"[HTTP] Served /metrics to {req.RemoteEndPoint}");
                    }
                    else if (req.HttpMethod == "GET" && req.Url.AbsolutePath.Equals("/info", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = new { name, mac, os };
                        var json = JsonSerializer.Serialize(info);
                        var bytes = Encoding.UTF8.GetBytes(json);
                        res.ContentType = "application/json";
                        res.StatusCode = (int)HttpStatusCode.OK;
                        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        res.Close();
                        Console.WriteLine($"[HTTP] Served /info to {req.RemoteEndPoint}");
                    }
                    else
                    {
                        res.StatusCode = (int)HttpStatusCode.NotFound;
                        var bytes = Encoding.UTF8.GetBytes("Not found");
                        res.OutputStream.Write(bytes, 0, bytes.Length);
                        res.Close();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HTTP] Handler error: {ex.Message}");
                }
            }, ct);
        }

        try { listener.Stop(); } catch { }
    }

    private static object CollectMetrics(string name, string mac, string os)
    {
        // CPU: sample PerformanceCounter (needs a small delay for accurate result)
        int cpuPercent = -1;
        try
        {
            using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpuCounter.NextValue();
            Thread.Sleep(400);
            cpuPercent = (int)Math.Round(cpuCounter.NextValue());
            cpuPercent = Math.Clamp(cpuPercent, 0, 100);
        }
        catch
        {
            cpuPercent = -1;
        }

        // RAM: Windows-only implementation using GlobalMemoryStatusEx
        long totalBytes = -1;
        long availMb = -1;
        int ramPercent = -1;
        try
        {
            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref mem))
            {
                totalBytes = (long)mem.ullTotalPhys;
                var availBytes = (long)mem.ullAvailPhys;
                availMb = availBytes / (1024L * 1024L);
                if (totalBytes > 0)
                {
                    ramPercent = (int)Math.Round(100.0 * (1.0 - ((double)availBytes / totalBytes)));
                    ramPercent = Math.Clamp(ramPercent, 0, 100);
                }
            }
        }
        catch
        {
            totalBytes = -1;
            availMb = -1;
            ramPercent = -1;
        }

        // GPU: try to sample GPU utilization via PerformanceCounter ("GPU Engine" -> "Utilization Percentage")
        int gpuPercent = -1;
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                // Sample available instances that look like GPU engines
                var engineInstances = instances.Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
                var counters = engineInstances
                    .Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i, readOnly: true))
                    .ToArray();

                // First NextValue() warm-up
                foreach (var c in counters) { try { c.NextValue(); } catch { } }
                Thread.Sleep(200);

                var samples = counters.Select(c =>
                {
                    try { return (int)Math.Round(c.NextValue()); }
                    catch { return -1; }
                }).Where(v => v >= 0).ToArray();

                foreach (var c in counters) c.Dispose();

                if (samples.Length > 0)
                {
                    // use max across engines as a conservative overall GPU utilization
                    gpuPercent = Math.Clamp(samples.Max(), 0, 100);
                }
            }
        }
        catch
        {
            gpuPercent = -1;
        }

        return new
        {
            name,
            mac,
            os,
            timestamp = DateTime.UtcNow,
            cpuPercent,
            ramPercent,
            availableRamMB = availMb,
            totalRamBytes = totalBytes,
            gpuPercent
        };
    }

    private static string? GetPrimaryMac()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                var hasIpv4 = ipProps.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (!hasIpv4) continue;

                var pa = ni.GetPhysicalAddress();
                var s = pa?.ToString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                var cleaned = Enumerable.Range(0, s.Length / 2)
                    .Select(i => s.Substring(i * 2, 2))
                    .ToArray();
                return string.Join(":", cleaned).ToUpperInvariant();
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    // Windows GlobalMemoryStatusEx P/Invoke
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}