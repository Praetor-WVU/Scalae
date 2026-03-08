using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalae.Data;
using Scalae.Logging;
using Scalae.Models;
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
                // Configure logging
                .AddLogging(builder =>
                {
                    builder.AddDebug();      // Logs to Debug Output window
                    builder.AddConsole();    // Logs to Console (if available)
                    builder.SetMinimumLevel(LogLevel.Debug);
                })
                // Register ILoggingService implementation
                .AddSingleton<ILoggingService, LoggingService>()
                // Register Database_Context
                .AddDbContext<Database_Context>()
                // Register DataCollection with logging
                .AddTransient<DataCollection>(sp => 
                    new DataCollection(sp.GetRequiredService<ILoggingService>()))
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
