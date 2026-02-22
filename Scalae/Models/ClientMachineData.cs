using System.ComponentModel.DataAnnotations;

namespace Scalae.Models
{
    public class ClientMachineData
    {
        public int Id { get; set; }
        
        public string MacAddress { get; set; }   
        
        // CPU Information
        public string CpuModel { get; set; }
        public double CpuUtilization { get; set; } // Percentage (0-100)
        
        // GPU Information
        public string GpuModel { get; set; }
        public double GpuUtilization { get; set; } // Percentage (0-100)
        
        // RAM Information
        public long RamTotalSize { get; set; } // In MB or GB
        public double RamUtilization { get; set; } // Percentage (0-100)
        
        // Storage Information
        public long StorageSize { get; set; } // In MB or GB
        
        // Optional: Timestamp for when this data was collected
        public DateTime Timestamp { get; set; }

        public ClientMachineData()
        {
           
        }

        public ClientMachineData(string macAddress, string cpuModel, double cpuUtilization, 
                                  string gpuModel, double gpuUtilization, long ramTotalSize, 
                                  double ramUtilization, long storageSize)
        {
            
            MacAddress = macAddress;
            CpuModel = cpuModel;
            CpuUtilization = cpuUtilization;
            GpuModel = gpuModel;
            GpuUtilization = gpuUtilization;
            RamTotalSize = ramTotalSize;
            RamUtilization = ramUtilization;
            StorageSize = storageSize;
            Timestamp = DateTime.UtcNow;
        }
    }
}