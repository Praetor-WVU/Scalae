using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Data.Repositories.EF
{
    public class ClientComputer
    {
        public int Id { get; set; }
        public string MacAddress { get; set; }
        public string ComputerName { get; set; }
        public string IpAddress { get; set; }
        public string OperatingSystem { get; set; }
        public bool IsActive { get; set; }

        public ClientComputer() 
        { 
        }
    }
}
