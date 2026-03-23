using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Scalae.Services
{
    public class MachineMonitoringService
    {
        private readonly IClientMachineRepository _repository;
        private readonly IMachineHistoryRepository _historyRepository;

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
            machine.LastRamUtilization = ParseUtilization(collectedData[1][1]);
            machine.LastGpuModel = collectedData[0][2];
            machine.LastGpuUtilization = ParseUtilization(collectedData[1][2]);
            machine.LastDataCollectionTime = DateTime.Now;

            _repository.Update(machine);
 
            CreateHistoryEntryAsync(machine);

        }

        private static double ParseUtilization(string value)
        {
            return double.TryParse(
                value.Replace("%", "").Replace("N/A", "-1"),
                out var result)
                    ? result
                    : -1;
        }

        public void CreateHistoryEntryAsync(ClientMachine machine)
        {

            var historyEntry = new MachineHistory
            {
                TimeStamp = machine.LastDataCollectionTime ?? DateTime.Now,
                Name = machine.Name,
                CpuUtilization = machine.LastCpuUtilization,
                RamUtilization = machine.LastRamUtilization,
                GpuUtilization = machine.LastGpuUtilization
            };

            _historyRepository.Create(historyEntry);

            Debug.WriteLine($"[MachineMonitoringService] Created history entry: Name={historyEntry.Name}, Time={historyEntry.TimeStamp}, Id={historyEntry.Id}");

          
        }
    }
}