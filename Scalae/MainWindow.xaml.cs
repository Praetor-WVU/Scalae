using Microsoft.EntityFrameworkCore;
using Scalae.Data;
using Scalae.Data.Repositories.EF;
using Scalae.Models;
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

                // Parse the collected data
                // data[0] = hardware names ["CPU Name", "RAM Size", "GPU Name"]
                // data[1] = utilization values ["45%", "62%", "23%"]
                
                var machineData = new ClientMachineData
                {
                    MacAddress = m.MACAddress,
                    CpuModel = data[0][0], // First hardware name
                    CpuUtilization = ParseUtilization(data[1][0]), // First utilization value
                    RamTotalSize = ParseRamSize(data[0][1]), // Second hardware name (e.g., "16 GB RAM")
                    RamUtilization = ParseUtilization(data[1][1]), // Second utilization value
                    GpuModel = data[0][2], // Third hardware name
                    GpuUtilization = ParseUtilization(data[1][2]), // Third utilization value
                    Timestamp = DateTime.UtcNow
                };

                // Save to database
                _db.ClientMachineData.Add(machineData);
                await _db.SaveChangesAsync(token);

                // Inside CollectOnceAsync after saving to DB
                Dispatcher.Invoke(() =>
                {
                    var existing = _machines.FirstOrDefault(x => x.IPAddress == m.IPAddress);
                    if (existing != null)
                    {
                        existing.LastCpuModel = machineData.CpuModel;
                        existing.LastCpuUtilization = machineData.CpuUtilization;
                        existing.LastGpuModel = machineData.GpuModel;
                        existing.LastGpuUtilization = machineData.GpuUtilization;
                        existing.LastRamUtilization = machineData.RamUtilization;
                        existing.LastDataCollectionTime = machineData.Timestamp;
                    }
                });
            }
        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            //Code to update the bargraphs with the data from the selected machine in the listbox.

            // In your ListBox_SelectionChanged or wherever you need latest data
            var selectedMachine = (ClientMachine)ListBoxMachines.SelectedItem;
            if (selectedMachine != null)
            {
                var latestData = _db.ClientMachineData
                    .Where(d => d.MacAddress == selectedMachine.MACAddress)
                    .OrderByDescending(d => d.Timestamp)
                    .FirstOrDefault();
                
                if (latestData != null)
                {
                    // Update your bar graphs with latestData.CpuUtilization, etc.
                }
            }
        }
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        // Helper to parse "45%" -> 45.0
        private double ParseUtilization(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "N/A")
                return 0.0;
            
            var cleaned = value.Replace("%", "").Trim();
            return double.TryParse(cleaned, out var result) ? result : 0.0;
        }

        // Helper to parse "16 GB RAM" -> 16384 (MB)
        private long ParseRamSize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && double.TryParse(parts[0], out var size))
            {
                // Convert GB to MB
                return (long)(size * 1024);
            }
            return 0;
        }
    }
}