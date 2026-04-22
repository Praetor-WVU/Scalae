using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal class Program
{
    private const int DiscoveryPort = 37020;
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

        var localPort = DiscoveryPort;
        if (args.Length > 0 && int.TryParse(args[0], out var p)) localPort = p;

        Console.WriteLine($"Scalae Agent starting. Listening UDP {localPort}. Ctrl+C to stop.");
        var mac = GetPrimaryMac() ?? "UNKNOWN";
        var os = RuntimeInformation.OSDescription?.Trim() ?? Environment.OSVersion.ToString();
        var name = Environment.MachineName;

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var receive = await udp.ReceiveAsync().WaitAsync(cts.Token);
                    var msg = Encoding.UTF8.GetString(receive.Buffer).Trim();
                    var remoteEP = receive.RemoteEndPoint;
                    Console.WriteLine($"Recv from {remoteEP.Address}:{remoteEP.Port}: '{msg}'");

                    if (string.Equals(msg, DiscoveryRequest, StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = $"{DiscoveryResponsePrefix}{name}|{mac}|{os}";
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        await udp.SendAsync(bytes, bytes.Length, remoteEP);
                        Console.WriteLine($"Responded to {remoteEP.Address}:{remoteEP.Port} -> {payload}");
                    }
                    else
                    {
                        // Optional: respond to unknown probes if desired (echo small name)
                        if (msg.Length <= 64)
                        {
                            var payload = $"{DiscoveryResponsePrefix}{msg}|{mac}|{os}";
                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await udp.SendAsync(bytes, bytes.Length, remoteEP);
                            Console.WriteLine($"Echo-responded to {remoteEP.Address}:{remoteEP.Port}");
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error receiving/sending UDP: {ex.Message}");
                    await Task.Delay(500, cts.Token).ContinueWith(_ => { });
                }
            }
        }
        finally
        {
            udp.Close();
        }

        Console.WriteLine("Agent stopped.");
        return 0;
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
}