using Microsoft.EntityFrameworkCore;
using Scalae.Data.Repositories.EF;
using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Data
{
    // Our database context class, inherits from DbContext
    public class Database_Context : DbContext
    {

         // Configs our database to use SQLite (needs nuget package Microsoft.EntityFrameworkCore.Sqlite)
                protected override void OnConfiguring(
                    DbContextOptionsBuilder optionsBuilder)
                => optionsBuilder.UseSqlite("Data Source=machines.db");
    
        // List Property for table(s), will have one for each table
        public DbSet<ClientComputerRepositoryEf> ClientComputers { get; set; } = default!;

       
 }
}
