using System;
using System.Collections.Generic;
using System.Text;
using System.Management;

namespace Scalae.Models
{
    /* DataCollection collects hardware information from Scalae clients using information from ClientMachine objects such as IP address and MAC address (preferred method). */
    public class DataCollection
    {
        /* fullCollect returns a jagged list of all hardware information. The first list includes the names of each hardware component. The second list it the corresponding utilization of each. */
        private static String[][] FullCollect(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            String[][] fullData = new String[2][];
            var names = new List<string>();
            var values = new List<string>();

            string cpuName = null;
            string gpuName = null;
            string ramName = null;

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                };

                var scope = new ManagementScope(scopePath, options);
                scope.Connect();

                // CPU name
                using (var cpuSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Win32_Processor")))
                {
                    foreach (ManagementObject mo in cpuSearcher.Get())
                    {
                        cpuName = mo["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(cpuName)) break;
                    }
                }

                // GPU name (first video controller)
                using (var gpuSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Win32_VideoController")))
                {
                    foreach (ManagementObject mo in gpuSearcher.Get())
                    {
                        gpuName = mo["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(gpuName)) break;
                    }
                }

                // RAM: total physical memory (bytes) and module count
                ulong totalBytes = 0;
                int moduleCount = 0;
                try
                {
                    using (var csSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")))
                    {
                        foreach (ManagementObject mo in csSearcher.Get())
                        {
                            if (mo["TotalPhysicalMemory"] != null)
                            {
                                totalBytes = Convert.ToUInt64(mo["TotalPhysicalMemory"]);
                                break;
                            }
                        }
                    }

                    using (var pmSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Manufacturer, Capacity FROM Win32_PhysicalMemory")))
                    {
                        foreach (ManagementObject mo in pmSearcher.Get())
                        {
                            moduleCount++;
                        }
                    }
                }
                catch { /* ignore RAM details failures */ }

                if (totalBytes > 0)
                {
                    double gb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                    ramName = $"{gb} GB RAM" + (moduleCount > 0 ? $" ({moduleCount} module{(moduleCount>1?"s":"")})" : string.Empty);
                }
            }
            catch
            {
                // Ignore WMI failures; fall back to defaults below
            }

            // fallback labels if no detailed name present
            names.Add(!string.IsNullOrWhiteSpace(cpuName) ? cpuName : "CPU");
            names.Add(!string.IsNullOrWhiteSpace(ramName) ? ramName : "RAM");
            names.Add(!string.IsNullOrWhiteSpace(gpuName) ? gpuName : "GPU");

            int cpu = UtilCPU(machine);
            int ram = UtilRAM(machine);
            int gpu = UtilGPU(machine);

            values.Add(cpu >= 0 ? $"{cpu}%" : "N/A");
            values.Add(ram >= 0 ? $"{ram}%" : "N/A");
            values.Add(gpu >= 0 ? $"{gpu}%" : "N/A");

            fullData[0] = names.ToArray();
            fullData[1] = values.ToArray();
            return fullData;
        }

        /* Fetches CPU utilization. */
        private static int UtilCPU(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                };

                var scope = new ManagementScope(scopePath, options);
                scope.Connect();

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT LoadPercentage FROM Win32_Processor")))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo["LoadPercentage"] != null)
                        {
                            return Convert.ToInt32(mo["LoadPercentage"]);
                        }
                    }
                }
            }
            catch
            {
                // fallthrough to unknown
            }

            return -1; // unknown/unavailable
        }

        /* Fetches RAM utilization. */
        private static int UtilRAM(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                };

                var scope = new ManagementScope(scopePath, options);
                scope.Connect();

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem")))
                {
                    foreach (ManagementObject mo in searcher.Get())
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
            }
            catch
            {
                // fallthrough to unknown
            }

            return -1; // unknown/unavailable
        }

        /* Fetches GPU utilization. */
        private static int UtilGPU(ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            try
            {
                var scopePath = string.IsNullOrWhiteSpace(machine.IPAddress) ? @"\\.\root\cimv2" : $@"\\{machine.IPAddress}\root\cimv2";
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.Default
                };

                var scope = new ManagementScope(scopePath, options);
                scope.Connect();

                // Try common perf class names that may expose GPU utilization
                var candidateQueries = new[]
                {
                    "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUPerf",
                    "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters",
                    "SELECT LoadPercentage FROM Win32_VideoController"
                };

                foreach (var q in candidateQueries)
                {
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(q)))
                        {
                            foreach (ManagementObject mo in searcher.Get())
                            {
                                if (mo["UtilizationPercentage"] != null)
                                    return Convert.ToInt32(mo["UtilizationPercentage"]);
                                if (mo["LoadPercentage"] != null)
                                    return Convert.ToInt32(mo["LoadPercentage"]);
                            }
                        }
                    }
                    catch
                    {
                        // try next query
                    }
                }
            }
            catch
            {
                // fallthrough to unknown
            }

            return -1; // unknown/unavailable
        }
    }
}
