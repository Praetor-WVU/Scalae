// X.Interfaces must reference the project where IClientComputerRepository is defined.
using Scalae.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Data.Repositories.EF
{
    // Our model class that represents a table in the database (this class represents the ClientComputers table)
    public class ClientComputerRepositoryEf : IClientComputerRepository
    {
        public int Id { get; set; }
        public string MacAddress { get; set; }
        public string ComputerName { get; set; }
        public string IpAddress { get; set; }
        public string OperatingSystem { get; set; }
        public bool IsActive { get; set; }

        // Parameterless constructor required by Entity Framework
        public ClientComputerRepositoryEf() 
        { 
        }
    }
}
