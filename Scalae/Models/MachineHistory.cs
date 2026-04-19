using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Scalae.Models
{
    public class MachineHistory
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public DateTime? TimeStamp { get; set; }
        public double? CpuUtilization { get; set; }
        public double? GpuUtilization { get; set; }
        public double? RamUtilization { get; set; }

        public MachineHistory() 
        { 
        
        }

        public MachineHistory(string name, DateTime? timeStamp, double? cpuUtilization, double? gpuUtilization, double? ramUtilization)
        {
            Id = Guid.NewGuid();
            Name = name;
            TimeStamp = timeStamp;
            CpuUtilization = cpuUtilization;
            GpuUtilization = gpuUtilization;
            RamUtilization = ramUtilization;
        }



    }
}
