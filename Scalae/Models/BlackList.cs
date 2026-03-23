using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Models
{
    public class BlackList
    {
        public int Id { get; set; }
        public string IPAddress { get; set; }
        public bool IsBlocked { get; set; }

        public BlackList() 
        {
        }

        public BlackList(string ipAddress, bool isBlocked)
        {
            IPAddress = ipAddress;
            IsBlocked = isBlocked;
        }
    }
}
