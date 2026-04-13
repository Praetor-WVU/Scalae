using Microsoft.EntityFrameworkCore;
using Scalae.Interfaces;
using Scalae.Models;

namespace Scalae.Data.Repositories.EF
{
    internal class MachineHistoryRepositoryEf: IMachineHistoryRepository
    {
        private readonly Database_Context _context;

        public MachineHistoryRepositoryEf(Database_Context context)
        {
            _context = context;
            _context.Database.EnsureCreated();
        }

        public IEnumerable<MachineHistory> List() =>
            _context.MachineHistories.AsNoTracking().ToList();

        public IEnumerable<MachineHistory> GetByName(string name) =>
            _context.MachineHistories
                .AsNoTracking()
                .Where(h => h.Name == name)
                .ToList();

        public MachineHistory? GetById(int id) =>
            _context.MachineHistories.Find(id);

        public void Create(MachineHistory history)
        {
            _context.MachineHistories.Add(history);
            _context.SaveChanges();
        }

        public void Delete(MachineHistory history)
        {
            _context.MachineHistories.Remove(history);
            _context.SaveChanges();
        }

        public bool Update(MachineHistory history)
        {
            try
            {
                _context.MachineHistories.Update(history);
                _context.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public async Task UpdateAsync(MachineHistory history)
        {
            _context.MachineHistories.Update(history);
            await _context.SaveChangesAsync();
        }
    }
}
