using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scalae.Logging;
using Scalae.WindowsIntegration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Scalae.Models
{
    /* DataCollection collects hardware information from Scalae clients using information from ClientMachine objects such as IP address and MAC address (preferred method). */
    public class DataCollection
    {
        private readonly ILogger<DataCollection> _logger;

        // Accept the logging abstraction via constructor injection.
        public DataCollection(ILoggingService? loggingService = null)
        {
            _logger = loggingService?.CreateLogger<DataCollection>() ?? NullLogger<DataCollection>.Instance;
        }

        /* fullCollect returns a jagged list of all hardware information. The first list includes the names of each hardware component. The second list it the corresponding utilization of each. */
        private String[][] fullCollect(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            _logger.LogInformation("Starting full hardware data collection for machine: {MachineName} (IP: {IPAddress})", machine.Name, machine.IPAddress);

            var names = new List<string>();
            var values = new List<string>();

            string cpuName = null;
            var gpuNames = new List<string>();
            string ramName = null;

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var scope = new ManagementScope(scopePath, new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                });
                scope.Connect();

                // CPU name
                foreach (var mo in SafeQuery(scope, "SELECT Name FROM Win32_Processor"))
                {
                    cpuName = mo["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(cpuName)) break;
                }

                // GPU names (enumerate all video controllers)
                foreach (var mo in SafeQuery(scope, "SELECT Name FROM Win32_VideoController"))
                {
                    var g = mo["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(g)) gpuNames.Add(g);
                }

                // RAM: total physical memory (bytes)
                foreach (var mo in SafeQuery(scope, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    if (mo["TotalPhysicalMemory"] != null)
                    {
                        var totalBytes = Convert.ToUInt64(mo["TotalPhysicalMemory"]);
                        double gb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                        ramName = $"{gb} GB RAM";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WMI hardware name collection failed for {Ip}. Using generic labels.", machine.IPAddress);
            }

            names.Add(!string.IsNullOrWhiteSpace(cpuName) ? cpuName : "CPU");
            names.Add(!string.IsNullOrWhiteSpace(ramName) ? ramName : "RAM");
            if (gpuNames.Count > 0) names.AddRange(gpuNames); else names.Add("GPU");

            int cpu = utilCPU(machine);
            int ram = utilRAM(machine);
            int gpu = utilGPU(machine);

            values.Add(cpu >= 0 ? $"{cpu}%" : "N/A");
            values.Add(ram >= 0 ? $"{ram}%" : "N/A");

            if (gpuNames.Count <= 1)
            {
                values.Add(gpu >= 0 ? $"{gpu}%" : "N/A");
            }
            else
            {
                // replicate aggregated GPU value per reported GPU name
                for (int i = 0; i < gpuNames.Count; i++)
                    values.Add(gpu >= 0 ? $"{gpu}%" : "N/A");
            }

            var fullData = new string[2][];
            fullData[0] = names.ToArray();
            fullData[1] = values.ToArray();

            _logger.LogInformation("Completed hardware data collection for machine: {MachineName} (IP: {IPAddress}).", machine.Name, machine.IPAddress);
            return fullData;
        }

        // Public wrapper - original behavior
        public String[][] CollectFull(ClientMachine machine) => CollectFull(machine, integration: null);

        // Overload that acknowledges Integration: denies collection if integration disallows access.
        public String[][] CollectFull(ClientMachine machine, Integration? integration, string? samAccountName = null, string? passwordIfNeeded = null)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            if (integration != null)
            {
                if (!integration.AllowsAccess(machine, samAccountName, passwordIfNeeded))
                {
                    _logger.LogWarning("Access denied by integration {Integration} for machine {Machine} (IP: {IP}).", integration, machine.Name, machine.IPAddress);
                    return new[] { new[] { "Access" }, new[] { "Denied" } };
                }
            }

            return fullCollect(machine);
        }

        /* Fetches CPU utilization. */
        private int utilCPU(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            var results = new List<int>();

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var scope = new ManagementScope(scopePath, new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                });
                scope.Connect();

                // Try a few WMI sources
                var queries = new[]
                {
                    "SELECT LoadPercentage FROM Win32_Processor",
                    "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'"
                };

                foreach (var q in queries)
                {
                    try
                    {
                        var mos = SafeQuery(scope, q);
                        foreach (var mo in mos)
                        {
                            if (mo.Properties["LoadPercentage"]?.Value != null &&
                                int.TryParse(mo["LoadPercentage"].ToString(), out var lp))
                                results.Add(Math.Clamp(lp, 0, 100));
                            else if (mo.Properties["PercentProcessorTime"]?.Value != null &&
                                     int.TryParse(mo["PercentProcessorTime"].ToString(), out var ppt))
                                results.Add(Math.Clamp(ppt, 0, 100));
                        }

                        if (results.Count > 0)
                        {
                            return (int) results.Max();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "WMI CPU query failed (query={Query}) on {Ip}; trying next.", q, machine.IPAddress);
                    }
                }

                // No WMI values found — log and, if local machine, attempt LibreHardwareMonitor fallback.
                _logger.LogDebug("No CPU metrics returned from WMI for {Ip}. Attempting local LibreHardwareMonitor fallback if applicable.", machine.IPAddress);

                if (string.IsNullOrWhiteSpace(machine.IPAddress) ||
                    machine.IPAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    machine.IPAddress == "127.0.0.1" || machine.IPAddress == "::1")
                {
                    try
                    {
                        // LibreHardwareMonitor fallback (requires LibreHardwareMonitorLib NuGet)
                        // using LibreHardwareMonitor.Hardware;
                        var cpuValue = FallbackCpuWithLibre();
                        if (cpuValue >= 0) return cpuValue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "LibreHardwareMonitor CPU fallback failed for local machine.");
                    }
                }
                else
                {
                    _logger.LogDebug("Skipping LibreHardwareMonitor fallback because machine {Ip} is remote.", machine.IPAddress);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CPU util collection failed for {Ip}", machine.IPAddress);
            }

            // nothing available
            _logger.LogInformation("CPU utilization unavailable for {Ip}; returning -1.", machine.IPAddress);
            return -1;
        }

        /* Fetches RAM utilization. */
        private int utilRAM(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var scope = new ManagementScope(scopePath, new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                });
                scope.Connect();

                foreach (var mo in SafeQuery(scope, "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                {
                    var freeObj = mo["FreePhysicalMemory"];
                    var totalObj = mo["TotalVisibleMemorySize"];
                    if (freeObj != null && totalObj != null)
                    {
                        double freeKb = Convert.ToDouble(freeObj);
                        double totalKb = Convert.ToDouble(totalObj);
                        if (totalKb > 0)
                        {
                            var usedPercent = (int)Math.Round((1.0 - (freeKb / totalKb)) * 100.0);
                            return Math.Clamp(usedPercent, 0, 100);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RAM util query failed for {Ip}", machine.IPAddress);
            }

            return -1; // unknown/unavailable
        }

        /* Fetches GPU utilization (aggregates results, supports multiple adapters, fallback hooks). */
        private int utilGPU(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            var results = new List<int>();

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var scope = new ManagementScope(scopePath, new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                });
                scope.Connect();

                var candidateQueries = new[]
                {
                    //In order of most likely to have data. Note: different GPU drivers and Windows versions expose
                    //GPU performance data differently, so we try multiple queries.
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUPerf",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters",
                    "SELECT * FROM Win32_VideoController"
                };

                foreach (var q in candidateQueries)
                {
                    try
                    {
                        var mos = SafeQuery(scope, q);
                        foreach (var mo in mos)
                        {
                            if (mo.Properties["UtilizationPercentage"]?.Value != null &&
                                int.TryParse(mo["UtilizationPercentage"].ToString(), out var up))
                                results.Add(Math.Clamp(up, 0, 100));
                            else if (mo.Properties["LoadPercentage"]?.Value != null &&
                                     int.TryParse(mo["LoadPercentage"].ToString(), out var lp))
                                results.Add(Math.Clamp(lp, 0, 100));
                        }

                        if (results.Count > 0) return results.Max(); // aggregate by max across adapters
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "WMI GPU query failed (query={Query}) on {Ip}; trying next.", q, machine.IPAddress);
                    }
                }

                _logger.LogDebug("No GPU metrics returned from WMI for {Ip}. Attempting local LibreHardwareMonitor fallback if applicable.", machine.IPAddress);

                if (string.IsNullOrWhiteSpace(machine.IPAddress) ||
                    machine.IPAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    machine.IPAddress == "127.0.0.1" || machine.IPAddress == "::1")
                {
                    try
                    {
                        var gpuValue = FallbackGpuWithLibre();
                        if (gpuValue >= 0) return gpuValue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "LibreHardwareMonitor GPU fallback failed for local machine.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GPU util collection failed for {Ip}", machine.IPAddress);
            }

            _logger.LogInformation("GPU utilization unavailable for {Ip}; returning -1.", machine.IPAddress);
            return -1;
        }

        // Helper: perform WMI query with small retry and return list of ManagementObject to avoid disposal pitfalls.
        private List<ManagementObject> SafeQuery(ManagementScope scope, string wql, int retries = 2, int delayMs = 200)
        {
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                    var coll = searcher.Get();
                    var results = new List<ManagementObject>();
                    foreach (ManagementObject mo in coll)
                        results.Add(mo);
                    return results;
                }
                catch (ManagementException mex) when (attempt < retries)
                {
                    _logger.LogDebug(mex, "WMI query '{Wql}' failed on attempt {Attempt}. Retrying after {Delay}ms.", wql, attempt + 1, delayMs);
                    Thread.Sleep(delayMs);
                }
            }

            // last attempt - will throw if it fails; caller handles exceptions
            using var finalSearcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            var finalColl = finalSearcher.Get();
            var finalList = new List<ManagementObject>();
            foreach (ManagementObject mo in finalColl)
                finalList.Add(mo);
            return finalList;
        }

        private int FallbackCpuWithLibre()
        {
            try
            {
                var computer = new LibreHardwareMonitor.Hardware.Computer()
                {
                    IsCpuEnabled = true
                };
                computer.Open();

                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu)
                    {
                        hw.Update();
                        foreach (var s in hw.Sensors)
                        {
                            // prefer "CPU Total" / Load sensors
                            if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && s.Value.HasValue)
                            {
                                var v = (int)Math.Round(s.Value.Value);
                                computer.Close();
                                return Math.Clamp(v, 0, 100);
                            }
                        }
                    }
                }

                computer.Close();
            }
            catch
            {
                // swallow; caller will log
            }
            return -1;
        }

        private int FallbackGpuWithLibre()
        {
            try
            {
                var computer = new LibreHardwareMonitor.Hardware.Computer()
                {
                    IsGpuEnabled = true
                };
                computer.Open();

                foreach (var hw in computer.Hardware)
                {
                    // Libre maps different GPU types; check for GpuNvidia/GpuAmd/GpuIntel via HardwareType.
                    if (hw.HardwareType.ToString().IndexOf("Gpu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hw.Update();
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && s.Value.HasValue)
                            {
                                var v = (int)Math.Round(s.Value.Value);
                                computer.Close();
                                return Math.Clamp(v, 0, 100);
                            }
                        }
                    }
                }

                computer.Close();
            }
            catch
            {
                // swallow; caller will log
            }
            return -1;
        }
    }
}
