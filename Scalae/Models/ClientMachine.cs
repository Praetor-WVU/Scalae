using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Models
{
    public class ClientMachine
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? MACAddress { get; set; }  // ← Changed to nullable
        public string? IPAddress { get; set; }
        public string? OperatingSystem { get; set; }
        public bool IsActive { get; set; }
        
        // New: Display latest hardware info
        public string? LastCpuModel { get; set; }
        public double? LastCpuUtilization { get; set; }
        public string? LastGpuModel { get; set; }
        public double? LastGpuUtilization { get; set; }
        public double? LastRamUtilization { get; set; }
        public DateTime? LastDataCollectionTime { get; set; }

        // Navigation property to all historical data
      

        public ClientMachine() 
        { 
        }

        public ClientMachine(string? name, string? macAddress, string? ipAddress, string? operatingSystem, bool isActive)
        {
            Name = name;
            MACAddress = macAddress;
            IPAddress = ipAddress;
            OperatingSystem = operatingSystem;
            IsActive = isActive;
        }

    }
}
