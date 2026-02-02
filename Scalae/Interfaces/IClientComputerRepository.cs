using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Controls;

namespace Scalae.Interfaces
{
    public interface IClientMachineRepository
    {
        void Create(ClientMachine clientMachine);

        bool Update(ClientMachine clientMachine);

        void Delete(ClientMachine clientMachine);
    }
}
