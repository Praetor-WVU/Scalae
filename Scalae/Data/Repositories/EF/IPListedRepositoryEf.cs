using Microsoft.EntityFrameworkCore;
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Data.Repositories.EF
{
    internal class IPListedRepositoryEf: IPListedRepository
    {
        private readonly Database_Context _context;
        public IPListedRepositoryEf(Database_Context context) 
        {
            _context = context;
            _context.Database.EnsureCreated();
        }

        public IEnumerable<BlackList> ListBlackList() =>
            _context.BlackLists.AsNoTracking().ToList();
        public IEnumerable<WhiteList> ListWhiteList() =>
            _context.WhiteLists.AsNoTracking().ToList();

        public void CreateBlackList(BlackList blackList)
        {
            _context.BlackLists.Add(blackList);
            _context.SaveChanges();
        }
        public void CreateWhiteList(WhiteList whiteList)
        {
            _context.WhiteLists.Add(whiteList);
            _context.SaveChanges();
        }

        public void DeleteBlackList(BlackList blackList)
        {
            _context.BlackLists.Remove(blackList);
            _context.SaveChanges();
        }

        public void DeleteWhiteList(WhiteList whiteList)
        {
            _context.WhiteLists.Remove(whiteList);
            _context.SaveChanges();
        }


        public bool UpdateBlackList(BlackList blackList)
        {
            try
            {
                _context.BlackLists.Update(blackList);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public bool UpdateWhiteList(WhiteList whiteList)
        {
            try
            {
                _context.WhiteLists.Update(whiteList);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }

        }

        public async Task UpdateBlackListAsync(BlackList blackList)
        {
            _context.BlackLists.Update(blackList);
            await _context.SaveChangesAsync();

        }

        public async Task UpdateWhiteListAsync(WhiteList whiteList)
        {
            _context.WhiteLists.Update(whiteList);
            await _context.SaveChangesAsync();
        }

        
       }
}
