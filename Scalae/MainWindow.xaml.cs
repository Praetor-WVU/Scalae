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
using System.Net;
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

            // Load whitelist and blacklist from database
            var whiteListData = _db.WhiteLists.AsNoTracking().ToList();
            _whiteList = new ObservableCollection<WhiteList>(whiteListData);

            var blackListData = _db.BlackLists.AsNoTracking().ToList();
            _blackList = new ObservableCollection<BlackList>(blackListData);

            // Bind the collection to the ListBox
            ListBoxMachines.ItemsSource = _machines;
            ListBoxHistory.ItemsSource = _machines;
            LBDetected.ItemsSource = _detectedMachines;
            LBWhitelist.ItemsSource = _whiteList;
            LBBlacklist.ItemsSource = _blackList;

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

            if (machine.LastGpuUtilization.HasValue)
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

        private ObservableCollection<ClientMachine> _detectedMachines = new ObservableCollection<ClientMachine>();

        private async void BtnScanNetwork_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Disable button during scan
                var button = (System.Windows.Controls.Button)sender;
                button.IsEnabled = false;
                button.Content = "Scanning...";

                // Clear previous detection results
                _detectedMachines.Clear();

                // Run network discovery on background thread
                var discovered = await Task.Run(() => ClientDetection.ClientDetectAuto(timeoutMs: 5000));

                // PING each discovered machine to populate ARP table, then re-query
                var enhanced = await Task.Run(() =>
                {
                    var results = new List<ClientMachine>();
                    foreach (var machine in discovered)
                    {
                        
                        if (string.IsNullOrWhiteSpace(machine.IPAddress))
                        {
                            results.Add(machine);
                            continue;
                        }

                        try
                        {
                            // Ping to populate ARP table
                            using var ping = new System.Net.NetworkInformation.Ping();
                            ping.Send(machine.IPAddress, 1000);

                            // Re-query with ClientDetectIP to get MAC from ARP
                            var enhanced = ClientDetection.ClientDetectIP(machine.IPAddress, timeoutMs: 2000);
                            results.Add(enhanced);
                        }
                        catch
                        {
                            // If enhancement fails, use original
                            results.Add(machine);
                        }
                    }
                    return results;
                });

                // Filter out machines already in the database
                var newMachines = _monitoringService.newMachineVerify(enhanced);

                // Get blacklisted and whitelisted IP addresses
                var blacklistedIPs = _db.BlackLists.Select(b => b.IPAddress).ToHashSet();
                var whitelistedIPs = _db.WhiteLists.Select(w => w.IPAddress).ToHashSet();

                // Add new machines to the detected list, excluding blacklisted and whitelisted machines
                foreach (var machine in newMachines)
                {
                    if (!blacklistedIPs.Contains(machine.IPAddress) && !whitelistedIPs.Contains(machine.IPAddress))
                    {
                        _detectedMachines.Add(machine);
                    }
                }

                // Show message if no new machines found
                if (_detectedMachines.Count == 0)
                {
                    System.Windows.MessageBox.Show("No new machines detected on the network.", "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error during network scan: {ex.Message}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Re-enable button
                var button = (System.Windows.Controls.Button)sender;
                button.IsEnabled = true;
                button.Content = "Scan Network";
            }
        }

        private async void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            if (LBDetected.SelectedItem is ClientMachine selectedMachine)
            {
                try
                {
                    // Disable button during processing
                    var button = (System.Windows.Controls.Button)sender;


                    // Add machine to database
                    _db.ClientMachines.Add(selectedMachine);
                    await _db.SaveChangesAsync();


                    var data = await Task.Run(() => _collector.CollectFull(selectedMachine));
                    _monitoringService.UpdateAndSaveMetrics(selectedMachine, data);

                    _machines.Add(selectedMachine);

                    _detectedMachines.Remove(selectedMachine);


                    System.Windows.MessageBox.Show($"Machine {selectedMachine.Name} has been accepted and added to monitoring.", "Machine Accepted", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error adding machine: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Please select a machine from the detected list first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnBlacklist_Click(object sender, RoutedEventArgs e)
        {
            if (LBDetected.SelectedItem is ClientMachine selectedMachine)
            {
                try
                {
                    // Add to blacklist
                    _db.BlackLists.Add(new BlackList
                    {
                        IPAddress = selectedMachine.IPAddress,
                        IsBlocked = true
                    });

                    _db.SaveChanges();

                    // Remove from detected machines
                    _detectedMachines.Remove(selectedMachine);

                    System.Windows.MessageBox.Show($"Machine {selectedMachine.Name} ({selectedMachine.IPAddress}) has been blacklisted.", "Machine Blacklisted", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error blacklisting machine: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Please select a machine from the detected list first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);  

            }

        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Selection selectwin = new Selection();
            selectwin.Show();

        }

        private async void AddMachine_WhiteList(object sender, RoutedEventArgs e)
        {
            var dialog = new AddWhitelistDialog { Owner = this };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    this.Cursor = System.Windows.Input.Cursors.Wait;
                    int successCount = 0;

                    foreach (var ipAddress in dialog.IPAddresses)
                    {
                        try
                        {
                            // Skip if already in database
                            if (_db.ClientMachines.Any(m => m.IPAddress == ipAddress))
                                continue;

                            // Skip if blacklisted
                            if (_db.BlackLists.Any(b => b.IPAddress == ipAddress))
                                continue;

                            // Skip if already whitelisted
                            if (_db.WhiteLists.Any(w => w.IPAddress == ipAddress))
                                continue;

                            // Detect machine
                            var machine = await Task.Run(() =>
                                ClientDetection.ClientDetectIP(ipAddress, timeoutMs: 5000));

                            if (machine == null) continue;

                            // Add to database and collect metrics
                            _db.ClientMachines.Add(machine);
                            await _db.SaveChangesAsync();

                            var data = await Task.Run(() => _collector.CollectFull(machine));
                            _monitoringService.UpdateAndSaveMetrics(machine, data);

                            // Add to whitelist
                            var newWhitelistEntry = new WhiteList { IPAddress = ipAddress, IsAllowed = true };
                            _db.WhiteLists.Add(newWhitelistEntry);
                            await _db.SaveChangesAsync();

                            // Update UI - Add to the ObservableCollection
                            _whiteList.Add(newWhitelistEntry);

                            // Update UI
                            _machines.Add(machine);
                            successCount++;
                        }
                        catch { /* Skip failed IPs */ }
                    }

                    System.Windows.MessageBox.Show("Added {successCount} of {dialog.IPAddresses.Count} machine(s) to monitoring.", "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    this.Cursor = System.Windows.Input.Cursors.Arrow;
                }
            }
        }


        private void RemoveMachine_WhiteList(object sender, RoutedEventArgs e)
        {
            if (LBWhitelist.SelectedItem is WhiteList selectedWhite)
            {
                try
                {
                    // Remove from whitelist
                    _db.WhiteLists.Remove(selectedWhite);
                    _db.SaveChanges();
                    // Remove from UI collection
                    _whiteList.Remove(selectedWhite);

                    System.Windows.MessageBox.Show($"IP {selectedWhite.IPAddress} has been removed from the whitelist.", "Whitelist Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error updating whitelist: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Please select an IP address from the whitelist first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void AddMachine_BlackList(object sender, RoutedEventArgs e)
        {
            var dialog = new AddBlackListDialog { Owner = this };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    this.Cursor = System.Windows.Input.Cursors.Wait;
                    int successCount = 0;

                    foreach (var ipAddress in dialog.IPAddresses)
                    {
                        try
                        {
                            // Skip if already blacklisted
                            if (_db.BlackLists.Any(b => b.IPAddress == ipAddress))
                                continue;

                            // Add to blacklist
                            var blackListEntry = new BlackList { IPAddress = ipAddress, IsBlocked = true };
                            _db.BlackLists.Add(blackListEntry);
                            await _db.SaveChangesAsync();

                            // Update UI collection
                            _blackList.Add(blackListEntry);

                            // Remove from detected machines if present
                            var detectedMachine = _detectedMachines.FirstOrDefault(m => m.IPAddress == ipAddress);
                            if (detectedMachine != null)
                            {
                                _detectedMachines.Remove(detectedMachine);
                            }

                            successCount++;
                        }
                        catch { /* Skip failed IPs */ }
                    }

                    System.Windows.MessageBox.Show($"Added {successCount} of {dialog.IPAddresses.Count} IP address(es) to blacklist.", "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    this.Cursor = System.Windows.Input.Cursors.Arrow;
                }
            }
        }

        private void RemoveMachine_BlackList(object sender, RoutedEventArgs e)
        {
            if (LBBlacklist.SelectedItem is BlackList selectedBlack)
            {
                try
                {
                    // Remove from blacklist
                    _db.BlackLists.Remove(selectedBlack);
                    _db.SaveChanges();
                    // Remove from UI collection
                    _blackList.Remove(selectedBlack);

                    System.Windows.MessageBox.Show($"IP {selectedBlack.IPAddress} has been removed from the blacklist.", "Blacklist Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error updating blacklist: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Please select an IP address from the blacklist first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}