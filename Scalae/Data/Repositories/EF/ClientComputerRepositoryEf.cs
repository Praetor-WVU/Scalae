using Microsoft.EntityFrameworkCore;
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scalae.Data.Repositories.EF
{
    // Our model class that represents a table in the database (this class represents the ClientComputers table)
    public class ClientComputerRepositoryEf : IClientComputerRepository
    {
        private readonly Database_Context _context;

        public ClientComputerRepositoryEf(Database_Context context)
        {
            _context = context;
            _context.Database.EnsureCreated();
        }

        public IEnumerable<ClientComputer> List() =>
            _context.ClientComputers.AsNoTracking().ToList();

        public ClientComputer? GetById(int id) =>
            _context.ClientComputers.Find(id);

        public void Create(ClientComputer clientComputer)
        {
            _context.ClientComputers.Add(clientComputer);
            _context.SaveChanges();
        }

        public bool Update(ClientComputer clientComputer)
        {
            try
            {
                _context.ClientComputers.Update(clientComputer);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public void Delete(ClientComputer clientComputer)
        {
            _context.ClientComputers.Remove(clientComputer);
            _context.SaveChanges();
        }

    }
}
