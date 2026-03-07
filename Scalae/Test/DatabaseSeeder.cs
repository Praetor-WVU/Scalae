using Microsoft.EntityFrameworkCore;
using Scalae.Data.Repositories.EF;
using Scalae.Models;
using System;
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

            // Use ClientDetection to get real MAC, IP, hostname, and OS info
            var localMachine = ClientDetection.ClientDetectIP("localhost", timeoutMs: 2000);

            var collector = new DataCollection();
            var results = collector.CollectFull(localMachine);

            // Populate the local machine with REAL collected data
            localMachine.LastCpuModel = results[0][0];  // Hardware name
            localMachine.LastCpuUtilization = double.TryParse(results[1][0].Replace("%", "").Replace("N/A", "-1"), out var cpu) ? cpu : -1;

            localMachine.LastRamUtilization = double.TryParse(results[1][1].Replace("%", "").Replace("N/A", "-1"), out var ram) ? ram : -1;

            localMachine.LastGpuModel = results[0][2];  // GPU hardware name
            localMachine.LastGpuUtilization = double.TryParse(results[1][2].Replace("%", "").Replace("N/A", "-1"), out var gpu) ? gpu : -1;

            localMachine.LastDataCollectionTime = DateTime.Now;

            // Add the local machine with REAL data to the database
            repo.Create(localMachine);
            var random = new Random();

            // CPU and GPU models for variety
            var cpuModels = new[]
            {
                "Intel Core i7-12700K",
                "AMD Ryzen 9 5950X",
                "Intel Core i5-11400",
                "AMD Ryzen 7 5800X",
                "Intel Core i9-13900K",
                "AMD Ryzen 5 5600X"
            };

            var gpuModels = new[]
            {
                "NVIDIA GeForce RTX 4080",
                "AMD Radeon RX 7900 XTX",
                "NVIDIA GeForce RTX 3060",
                "AMD Radeon RX 6800 XT",
                "NVIDIA GeForce RTX 4090",
                "AMD Radeon RX 6700 XT"
            };

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
                // Add random test data for hardware utilization with realistic patterns
                machine.LastCpuModel = cpuModels[random.Next(cpuModels.Length)];

                // CPU utilization - more varied patterns (idle, moderate, high load)
                var cpuPattern = random.Next(0, 3);
                machine.LastCpuUtilization = cpuPattern switch
                {
                    0 => Math.Round(random.NextDouble() * 25, 2),      // Idle: 0-25%
                    1 => Math.Round(25 + random.NextDouble() * 45, 2), // Moderate: 25-70%
                    _ => Math.Round(70 + random.NextDouble() * 30, 2)  // High load: 70-100%
                };

                machine.LastGpuModel = gpuModels[random.Next(gpuModels.Length)];

                // GPU utilization - often idle or heavily used (gaming/rendering)
                var gpuPattern = random.Next(0, 3);
                machine.LastGpuUtilization = gpuPattern switch
                {
                    0 => Math.Round(random.NextDouble() * 15, 2),      // Idle: 0-15%
                    1 => Math.Round(15 + random.NextDouble() * 35, 2), // Light: 15-50%
                    _ => Math.Round(80 + random.NextDouble() * 20, 2)  // Heavy: 80-100%
                };

                // RAM utilization - typically 30-90%
                machine.LastRamUtilization = Math.Round(30 + random.NextDouble() * 60, 2);

                // Last data collection time - varied within the last hour
                machine.LastDataCollectionTime = DateTime.Now.AddMinutes(-random.Next(1, 61));

                repo.Create(machine);
            }


        }

       
    }
}