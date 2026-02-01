using System.Windows;
using Scalae.Data;

namespace Scalae
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Seed test data (no-op if already present)
            DatabaseSeeder.Seed();

            base.OnStartup(e);
        }
    }
}