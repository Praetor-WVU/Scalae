using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scalae.Data;
using Scalae.ViewModels;
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

            var serviceProvider = new ServiceCollection()
                .AddSingleton<ViewModelMain>(sp =>
                    new ViewModelMain(async () =>
                    {
                        var db = sp.GetRequiredService<Database_Context>();
                        return await Task.Run(() => db.ClientMachines.AsNoTracking().ToList().AsEnumerable());
                    }))
                .AddSingleton<MainWindow>()
                .BuildServiceProvider();

            // Resolve the main window and show it
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }
    }
}
