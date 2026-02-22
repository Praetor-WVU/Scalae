using Scalae.Models;
using System.Collections.Generic;

namespace Scalae.Interfaces
{
    public interface IClientMachineDataRepository
    {
        IEnumerable<ClientMachineData> List();
        ClientMachineData? GetById(int id);
        void Create(ClientMachineData clientMachineData);
        bool Update(ClientMachineData clientMachineData);
        void Delete(ClientMachineData clientMachineData);
    }
}