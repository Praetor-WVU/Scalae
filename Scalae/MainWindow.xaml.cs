using Microsoft.EntityFrameworkCore;
using Scalae.Data;
using Scalae.Data.Repositories.EF;
using Scalae.Models;
using Scalae.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Scalae
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Database & collector fields (allow injection but preserve default behavior)
        private readonly Database_Context _db;
        private readonly DataCollection _collector;

        private ObservableCollection<ClientMachine> _machines = new ObservableCollection<ClientMachine>();

        // timer fields
        private PeriodicTimer? _periodicTimer;
        private CancellationTokenSource? _timerCts;
        private readonly TimeSpan _collectionInterval = TimeSpan.FromMinutes(5);
        private readonly SemaphoreSlim _collectLock = new SemaphoreSlim(1, 1);

        // Default constructor preserves existing behavior
        public MainWindow() : this(new Database_Context(), new DataCollection())
        {
        }

        // DI-friendly constructor that integrates with existing initialization
        public MainWindow(Database_Context db, DataCollection collector)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));

            InitializeComponent();
            InitializeWindow();
        }

        // Shared initialization logic (keeps all original setup lines)
        private void InitializeWindow()
        {
            // Ensure DB and tables exist
            _db.Database.EnsureCreated();
            this.SizeToContent = SizeToContent.WidthAndHeight;

            // Load data into an ObservableCollection so the UI updates when items change
            var list = _db.ClientMachines.AsNoTracking().ToList();
            _machines = new ObservableCollection<ClientMachine>(list);

            // Bind the collection to the ListBox
            ListBoxMachines.ItemsSource = _machines;

            // Start the periodic collection loop, closes on main window close.
            StartPeriodicCollection();
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            StopPeriodicCollection();
        }

        private void StartPeriodicCollection()
        {
            // cancel existing if any
            StopPeriodicCollection();

            _timerCts = new CancellationTokenSource();
            _periodicTimer = new PeriodicTimer(_collectionInterval);

            // run background loop (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await _periodicTimer.WaitForNextTickAsync(_timerCts.Token))
                    {
                        // avoid overlapping collects
                        if (!await _collectLock.WaitAsync(0, _timerCts.Token))
                            continue;

                        try
                        {
                            await CollectOnceAsync(_collector, _timerCts.Token);
                        }
                        finally
                        {
                            _collectLock.Release();
                        }
                    }
                }
                catch (OperationCanceledException) { /* shutting down */ }
                catch (Exception) { /* log if needed */ }
            });
        }

        private void StopPeriodicCollection()
        {
            try
            {
                _timerCts?.Cancel();
                _periodicTimer?.Dispose();
            }
            catch { }
            finally
            {
                _periodicTimer = null;
                _timerCts = null;
            }
        }

        // One run of data-collection across known DB machines.
        private async Task CollectOnceAsync(DataCollection collector, CancellationToken token)
        {
            var machines = _db.ClientMachines.AsNoTracking().ToList();

            foreach (var m in machines)
            {
                token.ThrowIfCancellationRequested();

                // run WMI-heavy work off the thread pool
                String[][] data = await Task.Run(() => collector.CollectFull(m), token);

                // update UI or database as needed. Example: refresh the ObservableCollection item (no properties currently to update).
                // If you add properties for last-check results, update them on the UI thread:

                // Inside CollectOnceAsync after saving to DB
                Dispatcher.Invoke(() =>
                {
                    var existing = _machines.FirstOrDefault(x => x.IPAddress == m.IPAddress);
                    if (existing == null)
                    {
                        _machines.Add(m);
                    }
                    else {
                        // Update properties on `existing` if you extend ClientMachine with fields for last collection
                    }
                });
        }
        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) 
        {
            //Code to update the bargraphs with the data from the selected machine in the listbox.
        }
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) { 
        
        }

       
        
    }
}