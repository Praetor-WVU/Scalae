// 
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Controls;

namespace Scalae.Data.Repositories.EF
{
    // Our model class that represents a table in the database (this class represents the ClientComputers table)
    public class ClientComputerRepositoryEf : IClientComputerRepository
    {
        private readonly Database_Context _context;

        public ClientComputerRepositoryEf(Database_Context context)
        {
            _context = context;
        }

        public void Create(ClientComputer clientComputer)
        {
            _context.ClientComputers.Add(clientComputer);
            _context.SaveChanges();
        }

        public void Delete(ClientComputer clientComputer)
        {
            _context.ClientComputers.Remove(clientComputer);
            _context.SaveChanges();
        }

        public ClientComputer? GetWithItemsById(int id) =>
        _context.ClientComputer.Include(o => o.Items).FirstOrDefault(o => o.Id == id);


        public IEnumerable<ClientComputer> Get() {
       


    }

        public bool Update(ClientComputer clientComputer)
        {
            try
            {
                _context.Update(order);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }
    }
