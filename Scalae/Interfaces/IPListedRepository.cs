using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Interfaces
{
    public interface IPListedRepository
    {
        public IEnumerable<BlackList> ListBlackList();
        public IEnumerable<WhiteList> ListWhiteList();
        public void CreateBlackList(BlackList blackList);
        public void CreateWhiteList(WhiteList whiteList);
        public void DeleteBlackList(BlackList blackList);

        public void DeleteWhiteList(WhiteList whiteList);
        public bool UpdateBlackList(BlackList blackList);
        public bool UpdateWhiteList(WhiteList whiteList);
       
    }
}
