using System.Linq;
using Scalae.Models;

namespace Scalae.Data
{
    public static class DatabaseSeeder
    {
        public static void Seed()
        {
            using var db = new Database_Context();
            db.Database.EnsureCreated();

            if (db.ClientMachines.Any())
                return; // already seeded

            var samples = new[]
            {
                new ClientMachine("WS-DEV-01", "00:11:22:33:44:55", "192.168.1.10", "Windows 11 Pro", true),
                new ClientMachine("WS-TEST-02", "00:AA:BB:CC:DD:01", "192.168.1.11", "Windows 10", false),
                new ClientMachine("LAPTOP-03", "12:34:56:78:9A:BC", "192.168.1.12", "Ubuntu 24.04", true)
            };

            db.ClientMachines.AddRange(samples);
            db.SaveChanges();
        }
    }
}