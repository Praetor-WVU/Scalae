using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Scalae.Interfaces
{
    public interface IMachineHistoryRepository
    {
        IEnumerable<MachineHistory> List();
        MachineHistory? GetById(int id);
        void Create(MachineHistory history);
        void Delete(MachineHistory history);
        bool Update(MachineHistory history);
        Task UpdateAsync(MachineHistory history);
    }
}
