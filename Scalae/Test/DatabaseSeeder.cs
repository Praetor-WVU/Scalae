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

            if (db.ClientComputers.Any())
                return; // already seeded

            var samples = new[]
            {
                new ClientComputer { MacAddress = "00:11:22:33:44:55", ComputerName = "WS-DEV-01", IpAddress = "192.168.1.10", OperatingSystem = "Windows 11 Pro", IsActive = true },
                new ClientComputer { MacAddress = "00:AA:BB:CC:DD:01", ComputerName = "WS-TEST-02", IpAddress = "192.168.1.11", OperatingSystem = "Windows 10", IsActive = false },
                new ClientComputer { MacAddress = "12:34:56:78:9A:BC", ComputerName = "LAPTOP-03", IpAddress = "192.168.1.12", OperatingSystem = "Ubuntu 24.04", IsActive = true }
            };

            db.ClientComputers.AddRange(samples);
            db.SaveChanges();
        }
    }
}