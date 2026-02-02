using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Controls;

namespace Scalae.Interfaces
{
    public interface IClientComputerRepository
    {
        void Create(ClientComputer clientComputer);

        bool Update(ClientComputer clientComputer);

        void Delete(ClientComputer clientComputer);
    }
}
