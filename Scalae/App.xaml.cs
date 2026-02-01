using Scalae.Data;
using System.Configuration;
using System.Data;
using System.Windows;

namespace Scalae
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
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
