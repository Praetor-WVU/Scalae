using Scalae.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Controls;

namespace Scalae.Interfaces
{
    public interface IClientComputerRepository
    {
       
        IEnumerable<ClientComputer> ListWithItems();

        ClientComputer? GetWithItemsById(int id);

        void Create(Order order);

        bool Update(Order order);

        void Delete(Order order);
    }
}
