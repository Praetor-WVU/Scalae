using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae
{
    public class ClientMachine
    {
        public string Name { get; set; }
        public string MACAddress { get; set; }
        public string IPAddress { get; set; }
        public string OperatingSystem { get; set; }
        public bool IsActive { get; set; }
        public ClientMachine(string name, string macAddress, string ipAddress, string operatingSystem, bool isActive)
        {
            Name = name;
            MACAddress = macAddress;
            IPAddress = ipAddress;
            OperatingSystem = operatingSystem;
            IsActive = isActive;
        }
    }
}
