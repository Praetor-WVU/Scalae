using Scalae.Data;
using Scalae.Data.Repositories.EF;
using Scalae.Interfaces;
using Scalae.Models;
using System;
using System.Threading.Tasks;

namespace Scalae.Services
{
    public class MachineMonitoringService
    {
        private readonly IClientMachineRepository _repository;

        public MachineMonitoringService(IClientMachineRepository repository)
        {
            _repository = repository;
        }

        /// <summary>
        /// Synchronous version for backwards compatibility
        /// </summary>
        public void UpdateAndSaveMetrics(ClientMachine machine, string[][] collectedData)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            if (collectedData == null || collectedData.Length < 2) 
                throw new ArgumentException("Invalid collected data format", nameof(collectedData));

           
 
               
                var history = new MachineHistory(
                    machine.Name ?? "Unknown",
                    machine.LastDataCollectionTime,
                    machine.LastCpuUtilization,
                    machine.LastGpuUtilization,
                    machine.LastRamUtilization
                );
                var historyRepo = new MachineHistoryRepositoryEf(new Database_Context());
                historyRepo.Create(history);

            machine.LastCpuModel = collectedData[0][0];
            machine.LastCpuUtilization = ParseUtilization(collectedData[1][0]);
            machine.LastRamUtilization = ParseUtilization(collectedData[1][1]);
            machine.LastGpuModel = collectedData[0][2];
            machine.LastGpuUtilization = ParseUtilization(collectedData[1][2]);
            machine.LastDataCollectionTime = DateTime.Now;
            
            _repository.Update(machine);
        }

        private static double ParseUtilization(string value)
        {
            return double.TryParse(
                value.Replace("%", "").Replace("N/A", "-1"), 
                out var result) 
                    ? result 
                    : -1;
        }
    }
}