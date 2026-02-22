using Microsoft.EntityFrameworkCore;
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scalae.Data.Repositories.EF
{
    public class ClientMachineDataRepositoryEf : IClientMachineDataRepository
    {
        private readonly Database_Context _context;

        public ClientMachineDataRepositoryEf(Database_Context context)
        {
            _context = context;
            _context.Database.EnsureCreated();
        }

        public IEnumerable<ClientMachineData> List() =>
            _context.ClientMachineData.AsNoTracking().ToList();

        public ClientMachineData? GetById(int id) =>
            _context.ClientMachineData.Find(id);


        public void Create(ClientMachineData clientMachineData)
        {
            _context.ClientMachineData.Add(clientMachineData);
            _context.SaveChanges();
        }

        public bool Update(ClientMachineData clientMachineData)
        {
            try
            {
                _context.ClientMachineData.Update(clientMachineData);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public void Delete(ClientMachineData clientMachineData)
        {
            _context.ClientMachineData.Remove(clientMachineData);
            _context.SaveChanges();
        }
    }
}