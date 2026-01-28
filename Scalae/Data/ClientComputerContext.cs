using Microsoft.EntityFrameworkCore;
using Scalae.Data.Repositories;
using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Data
{
    public class ClientComputerContext : DbContext
    {
        public DbSet<ClientComputer> ClientComputers { get; set; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data source = machines.db");
        }
    }
}
