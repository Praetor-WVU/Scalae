using Microsoft.EntityFrameworkCore;
using Scalae.Data.Repositories.EF;
using Scalae.Models;
using System.Linq;

namespace Scalae.Data
{
    public static class DatabaseSeeder
    {
        public static void Seed()
        {
            using var db = new Database_Context();
            db.Database.EnsureCreated();
            
            // Check if data already exists - if so, skip seeding
            if (db.ClientMachines.Any())
            {
                return; // Database has been seeded
            }
            
            var repo = new ClientMachineRepositoryEf(db);

            // Define all machines to seed
            var machinesToSeed = new[]
            {
                new ClientMachine("WS-DEV-03", "00:11:22:33:44:66", "192.168.1.13", "Windows 11 Pro", true),
                new ClientMachine("WS-TEST-04", "00:AA:BB:CC:DD:02", "192.168.8.14", "Windows 10", false),
                new ClientMachine("LAPTOP-04", "12:34:56:78:9A:CD", "192.168.8.15", "Ubuntu 24.04", true),
                new ClientMachine("WS-DEV-05", "00:11:22:33:44:77", "192.168.8.16", "Windows 11 Pro", true),
                new ClientMachine("WS-TEST-05", "00:AA:BB:CC:DD:03", "192.168.8.17", "Windows 10", false),
                new ClientMachine("WS-DEV-01", "00:11:22:33:44:55", "192.168.1.10", "Windows 11 Pro", true),
                new ClientMachine("WS-TEST-02", "00:AA:BB:CC:DD:01", "192.168.1.11", "Windows 10", false),
                new ClientMachine("LAPTOP-03", "12:34:56:78:9A:BC", "192.168.1.12", "Ubuntu 24.04", true)
            };

            foreach (var machine in machinesToSeed)
            {
                repo.Create(machine);
            }

            
        }
    }
}