using Scalae.Data;
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;

namespace Scalae.Services
{
    public class MachineMonitoringService
    {
        private readonly IClientMachineRepository _repository;
        private readonly IMachineHistoryRepository _historyRepository;
        private ObservableCollection<MachineHistory> hist;


        public MachineMonitoringService(IClientMachineRepository repository, IMachineHistoryRepository historyRepository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
        }

        /// <summary>
        /// Synchronous version for backwards compatibility
        /// </summary>
        public void UpdateAndSaveMetrics(ClientMachine machine, string[][] collectedData)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            if (collectedData == null || collectedData.Length < 2)
                throw new ArgumentException("Invalid collected data format", nameof(collectedData));

           

            machine.LastCpuModel = collectedData[0][0];
            machine.LastCpuUtilization = ParseUtilization(collectedData[1][0]);
            machine.LastRamModel = collectedData[0][1];
            machine.LastRamUtilization = ParseUtilization(collectedData[1][1]);
            machine.LastGpuModel = collectedData[0][2];
            machine.LastGpuUtilization = ParseUtilization(collectedData[1][2]);
            machine.LastDataCollectionTime = DateTime.Now;

            _repository.Update(machine);
 
            CreateHistoryEntryAsync(machine);

        }

        private static double ParseUtilization(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "N/A")
                return double.NaN; // Use NaN instead of -1 for invalid data

            return double.TryParse(value.Replace("%", ""), out var result)
                ? result
                : double.NaN;
        }

        public void CreateHistoryEntryAsync(ClientMachine machine)
        {
            // Only create history entry if at least one valid metric exists
            if (!IsValidUtilization(machine.LastCpuUtilization) && 
                !IsValidUtilization(machine.LastRamUtilization) && 
                !IsValidUtilization(machine.LastGpuUtilization))
            {
                Debug.WriteLine($"[MachineMonitoringService] Skipping history entry for {machine.Name} - no valid metrics");
                return;
            }

            var historyEntry = new MachineHistory
            {
                TimeStamp = machine.LastDataCollectionTime ?? DateTime.Now,
                Name = machine.Name,
                CpuUtilization = IsValidUtilization(machine.LastCpuUtilization) ? machine.LastCpuUtilization : null,
                RamUtilization = IsValidUtilization(machine.LastRamUtilization) ? machine.LastRamUtilization : null,
                GpuUtilization = IsValidUtilization(machine.LastGpuUtilization) ? machine.LastGpuUtilization : null
            };

            _historyRepository.Create(historyEntry);

            Debug.WriteLine($"[MachineMonitoringService] Created history entry: Name={historyEntry.Name}, Time={historyEntry.TimeStamp}, Id={historyEntry.Id}");

          
        }

        private static bool IsValidUtilization(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && value.Value >= 0 && value.Value <= 100;
        }

        //Call to retun a list of the history of a machine, used in the UI to display the history of a machine (returns entries for passed param name)
        public ObservableCollection<MachineHistory> GetHistoryList(string name)
        {
            var list = _historyRepository.GetByName(name);
            hist = new ObservableCollection<MachineHistory>(list);

            return hist;
        }

        /// <summary>
        /// Verifies and returns a list of newly detected machines that are not already in the repository.
        /// This is a helper method for client detection workflow.
        /// </summary>
        /// <param name="detectedMachines">List of machines detected via ClientDetection</param>
        /// <returns>Collection of new machines not yet in the database</returns>
        public ObservableCollection<ClientMachine> newMachineVerify(IEnumerable<ClientMachine> detectedMachines)
        {
            if (detectedMachines == null)
                throw new ArgumentNullException(nameof(detectedMachines));

            var newMachines = new ObservableCollection<ClientMachine>();
            var existingMachines = _repository.GetAll();

            foreach (var detected in detectedMachines)
            {
                // Check if machine already exists by IP or MAC address
                bool exists = existingMachines.Any(m =>
                    (!string.IsNullOrEmpty(detected.IPAddress) && m.IPAddress == detected.IPAddress) ||
                    (!string.IsNullOrEmpty(detected.MACAddress) && m.MACAddress == detected.MACAddress));

                if (!exists && detected.IsActive)
                {
                    newMachines.Add(detected);
                    Debug.WriteLine($"[MachineMonitoringService] New machine detected: {detected.Name} ({detected.IPAddress})");
                }
            }

            return newMachines;
        }

        /// <summary>
        /// Adds a machine to the whitelist (saves to repository as monitored machine)
        /// </summary>
        public void AddToWhitelist(ClientMachine machine)
        {
            if (machine == null)
                throw new ArgumentNullException(nameof(machine));

            _repository.Create(machine);
            Debug.WriteLine($"[MachineMonitoringService] Added machine to whitelist: {machine.Name} ({machine.IPAddress})");
        }

        /// <summary>
        /// Adds multiple machines to the whitelist
        /// </summary>
        public void AddToWhitelist(IEnumerable<ClientMachine> machines)
        {
            if (machines == null)
                throw new ArgumentNullException(nameof(machines));

            foreach (var machine in machines)
            {
                AddToWhitelist(machine);
            }
        }
    }
}