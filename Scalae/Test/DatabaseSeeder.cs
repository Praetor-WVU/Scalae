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
            // Creates a new instance of the database context, ensure database and tables are created, and creates new repository instance so Create(ClientMachine) method can be called
            using var db = new Database_Context();
            db.Database.EnsureCreated();
            var repo = new ClientMachineRepositoryEf(db);
            var dataRepo = new ClientMachineDataRepositoryEf(db);

            //Example of how to add a single machine, but we will seed multiple below
            //repo.Create(new ClientMachine("WS-DEV-03", "00:11:22:33:44:66", "192.168.1.13", "Windows 11 Pro", true));

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

            // Add each machine using the repository
            foreach (var machine in machinesToSeed)
            {
                repo.Create(machine);
            }

            // Define all machine data to seed
            var machineDataToSeed = new[]
            {
                new ClientMachineData("00:11:22:33:44:66", "Intel Core i7-12700K", 45.5, "NVIDIA RTX 3070", 30.2, 32768, 60.0, 1024000),
                new ClientMachineData("00:AA:BB:CC:DD:02", "AMD Ryzen 9 5900X", 55.3, "NVIDIA RTX 4080", 25.8, 16384, 45.5, 512000),
                new ClientMachineData("12:34:56:78:9A:CD", "Intel Core i5-11400", 35.7, "AMD RX 6800 XT", 40.1, 16384, 50.3, 512000),
                new ClientMachineData("00:11:22:33:44:77", "AMD Ryzen 7 5800X", 50.2, "NVIDIA GTX 1660 Ti", 35.6, 32768, 55.7, 1024000),
                new ClientMachineData("00:AA:BB:CC:DD:03", "Intel Core i9-13900K", 65.8, "AMD RX 7900 XTX", 45.3, 65536, 70.2, 2048000),
                new ClientMachineData("00:11:22:33:44:55", "Intel Core i7-12700K", 42.1, "NVIDIA RTX 3070", 28.5, 32768, 58.3, 1024000),
                new ClientMachineData("00:AA:BB:CC:DD:01", "AMD Ryzen 9 5900X", 48.9, "NVIDIA RTX 4080", 32.7, 16384, 48.1, 512000),
                new ClientMachineData("12:34:56:78:9A:BC", "Intel Core i5-11400", 38.4, "AMD RX 6800 XT", 38.9, 16384, 52.6, 512000)
            };

            // Add each machine data using the repository
            foreach (var machineData in machineDataToSeed)
            {
                dataRepo.Create(machineData);
            }
        }
    }
}