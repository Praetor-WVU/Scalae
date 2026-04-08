using Microsoft.EntityFrameworkCore;
using Scalae.Data;
using Scalae.Data.Repositories.EF;
using Scalae.Models;
using Scalae.Services;
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
using System.Windows.Controls.DataVisualization.Charting;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Scalae
{
    /// <summary>
    /// Interaction logic for MainWindow.Xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        public List<DataPoint> RAMLine { get; set; }
        public List<DataPoint> GPULine { get; set; }
        public List<DataPoint> CPULine { get; set; }
        //temporary data for testing the chart, will be removed when we implement the actual data collection and chart updating logic.

        // Database & collector fields (allow injection but preserve default behavior)
        private readonly Database_Context _db;
        private readonly DataCollection _collector;
        private readonly MachineMonitoringService _monitoringService;

        private ObservableCollection<ClientMachine> _machines = new ObservableCollection<ClientMachine>();

        // Not currently used in the UI, but could be exposed for a history view whenever necessary ui is updated to handle it
        private ObservableCollection<MachineHistory> _machineHistory = new ObservableCollection<MachineHistory>();

        //Collections for white and black list
        private ObservableCollection<WhiteList> _whiteList = new ObservableCollection<WhiteList>();
        private ObservableCollection<BlackList> _blackList = new ObservableCollection<BlackList>();

        // timer fields
        private PeriodicTimer? _periodicTimer;
        private CancellationTokenSource? _timerCts;
        private readonly TimeSpan _collectionInterval = TimeSpan.FromMinutes(.167);
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
            _monitoringService = new MachineMonitoringService(new ClientMachineRepositoryEf(_db), new MachineHistoryRepositoryEf(_db));

            InitializeComponent();
            InitializeWindow();
        }

        // Shared initialization logic (keeps all original setup lines)
        private void InitializeWindow()
        {
            RAMLine = new List<DataPoint>
            {
                new DataPoint { Date = "2/24/26 - 12:00:00", Value = 78},
                new DataPoint { Date = "2/24/26 - 13:00:00", Value = 87},
                new DataPoint { Date = "3/25/26 - 14:00:00", Value = 73},
            };

            GPULine = new List<DataPoint>
            {
                new DataPoint { Date = "2/24/26 - 12:00:00", Value = 89},
                new DataPoint { Date = "2/24/26 - 13:00:00", Value = 85},
                new DataPoint { Date = "3/25/26 - 14:00:00", Value = 77},
            };

            CPULine = new List<DataPoint>
            {
                new DataPoint { Date = "2/24/26 - 12:00:00", Value = 87},
                new DataPoint { Date = "2/24/26 - 13:00:00", Value = 76},
                new DataPoint { Date = "3/25/26 - 14:00:00", Value = 80},
            };

            DataContext = this; // Bind data to XAML
            //^ Temporary data for testing the chart, will be removed when we implement the actual data collection and chart updating logic.



            // Ensure DB and tables exist
            _db.Database.EnsureCreated();
            this.SizeToContent = SizeToContent.WidthAndHeight;

            // Load data into an ObservableCollection so the UI updates when items change
            var list = _db.ClientMachines.AsNoTracking().ToList();
            _machines = new ObservableCollection<ClientMachine>(list);

            // Bind the collection to the ListBox
            ListBoxMachines.ItemsSource = _machines;
            ListBoxHistory.ItemsSource = _machines;


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
            // Create a fresh DbContext and repository instances for the background collection run.
            // DbContext (Database_Context) is not thread-safe and must not be shared between UI and background threads.
            using var runDb = new Database_Context();
            var runMonitoringService = new MachineMonitoringService(new Data.Repositories.EF.ClientMachineRepositoryEf(runDb), new Data.Repositories.EF.MachineHistoryRepositoryEf(runDb));

            var machines = runDb.ClientMachines.AsNoTracking().ToList();

            foreach (var m in machines)
            {
                token.ThrowIfCancellationRequested();

                // run WMI-heavy work off the thread pool
                String[][] data = await Task.Run(() => collector.CollectFull(m), token);

                // Use monitoring service (backed by the fresh DbContext) to update and save
                runMonitoringService.UpdateAndSaveMetrics(m, data);


                // Update UI with the latest values from the collected machine
                Dispatcher.Invoke(() =>
                {
                    var existing = _machines.FirstOrDefault(x => x.IPAddress == m.IPAddress);
                    if (existing == null)
                    {
                        _machines.Add(m);
                    }
                    else
                    {
                        // Update properties on `existing` if you extend ClientMachine with fields for last collection
                        existing.LastCpuModel = m.LastCpuModel;
                        existing.LastCpuUtilization = m.LastCpuUtilization;
                        existing.LastRamUtilization = m.LastRamUtilization;
                        existing.LastGpuModel = m.LastGpuModel;
                        existing.LastGpuUtilization = m.LastGpuUtilization;
                        existing.LastDataCollectionTime = m.LastDataCollectionTime;

                        // Refresh chart if this is the currently selected machine
                        if (ListBoxMachines.SelectedItem is ClientMachine selectedMachine &&
                            selectedMachine.IPAddress == m.IPAddress)
                        {
                            UpdateChart(existing);
                        }


                    }
                });

            }
        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //Code to update the bargraphs with the data from the selected machine in the listbox.
            if (ListBoxMachines.SelectedItem is ClientMachine selectedMachine)
            {
                UpdateChart(selectedMachine);
            }

        }

        private void UpdateChart(ClientMachine machine)
        {
            var chartData = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>();

            // Cpu bargraph data

            if (machine.LastCpuUtilization.HasValue)
            {
                chartData.Add(new System.Collections.Generic.KeyValuePair<string, double>("CPU Usage", machine.LastCpuUtilization.Value));
            }
            else
            {
                chartData.Add(new System.Collections.Generic.KeyValuePair<string, double>("Usage", 0));

            }

            // Ram bargraph data
            if (machine.LastRamUtilization.HasValue)
            {
                chartData.Add(new System.Collections.Generic.KeyValuePair<string, double>("RAM Usage", machine.LastRamUtilization.Value));
            }
            else
            {
                chartData.Add(new System.Collections.Generic.KeyValuePair<string, double>("Usage", 0));
            }

            // Gpu bargraph data

            if (machine.LastCpuUtilization.HasValue)
            {
                chartData.Add(new System.Collections.Generic.KeyValuePair<string, double>("GPU Usage", machine.LastGpuUtilization.Value));
            }
            else
            {
                chartData.Add(new System.Collections.Generic.KeyValuePair<string, double>("Usage", 0));
            }

            HardwareSeries.ItemsSource = chartData;
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void ListBoxHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListBoxHistory.SelectedItem is ClientMachine selectedMachine)
            {
                _machineHistory = _monitoringService.GetHistoryList(selectedMachine.Name);
                
                
                // Needs formatting 

                    MaxCpuUtilization.Text = _machineHistory.Max(h => h.CpuUtilization ?? 0).ToString();
                    MaxRamUtilization.Text = _machineHistory.Max(h => h.RamUtilization ?? 0).ToString();
                    MaxGpuUtilization.Text = _machineHistory.Max(h => h.GpuUtilization ?? 0).ToString();

                    MinCpuUtilization.Text = _machineHistory.Min(h => h.CpuUtilization ?? 0).ToString();
                    MinRamUtilization.Text = _machineHistory.Min(h => h.RamUtilization ?? 0).ToString();
                    MinGpuUtilization.Text = _machineHistory.Min(h => h.GpuUtilization ?? 0).ToString();

                    AverageCpuUtilization.Text = _machineHistory.Average(h => h.CpuUtilization ?? 0).ToString();
                    AverageRamUtilization.Text = _machineHistory.Average(h => h.RamUtilization ?? 0).ToString();
                    AverageGpuUtilization.Text = _machineHistory.Average(h => h.GpuUtilization ?? 0).ToString();


            }
                
        }

       

        public class DataPoint
        {
            public string Date { get; set; }
            public double Value { get; set; }
        }

        //^ Temporary class for testing the chart, will be removed when we implement the actual data collection and chart updating logic.

    }
}