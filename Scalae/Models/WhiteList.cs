using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Models
{
    public class WhiteList
    {
        public int Id { get; set; }
        public string IPAddress { get; set; }
        public bool IsAllowed { get; set; }

        public WhiteList() 
        {
        }

        public WhiteList(string ipAddress, bool isAllowed)
        {
            IPAddress = ipAddress;
            IsAllowed = isAllowed;
        }

    }
}
