using Microsoft.EntityFrameworkCore;
using Scalae.Models;

namespace Scalae.Data
{
    // Our database context class, inherits from DbContext
    public class Database_Context : DbContext
    {
        // Config our database to use SQLite (needs nuget package Microsoft.EntityFrameworkCore.Sqlite)
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite("Data Source=machines.db");

        // One DbSet per table
        public DbSet<ClientMachine> ClientMachines { get; set; } = default!;
        public DbSet<ClientMachineData> ClientMachineData { get; set; } = default!;
    }
}
