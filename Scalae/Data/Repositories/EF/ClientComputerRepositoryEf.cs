using Microsoft.EntityFrameworkCore;
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scalae.Data.Repositories.EF
{
    // Our model class that represents a table in the database (this class represents the ClientComputers table)
    public class ClientMachineRepositoryEf : IClientMachineRepository
    {
        private readonly Database_Context _context;

        public ClientMachineRepositoryEf(Database_Context context)
        {
            _context = context;
            _context.Database.EnsureCreated();
        }

        public IEnumerable<ClientMachine> List() =>
            _context.ClientMachines.AsNoTracking().ToList();

        public ClientMachine? GetById(int id) =>
            _context.ClientMachines.Find(id);

        public void Create(ClientMachine clientMachine)
        {
            // Validation for data collextion alls
                _context.ClientMachines.Add(clientMachine);
                _context.SaveChanges();
            
        }

        public bool Update(ClientMachine clientMachine)
        {
            try
            {
                _context.ClientMachines.Update(clientMachine);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public void Delete(ClientMachine clientMachine)
        {
            _context.ClientMachines.Remove(clientMachine);
            _context.SaveChanges();
        }

    }
}
