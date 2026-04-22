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
        private readonly TimeSpan _collectionInterval = TimeSpan.FromMinutes(.05);
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
            
            // Initialize empty plot
            Loaded += (s, e) =>
            {
                WpfPlot1.Plot.Title("Select a machine to view history");
                WpfPlot1.Refresh();
            };
        }

        // Shared initialization logic (keeps all original setup lines)
        private void InitializeWindow()
        {
           

            DataContext = this; // Bind data to XAML
            //^ Temporary data for testing the chart, will be removed when we implement the actual data collection and chart updating logic.Â 


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
                
                // Apply date filter and update plot
                ApplyDateFilterAndUpdatePlot();
            }
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
                var discovered = await Task.Run(() => ClientDetection.ClientDetectAutoAsync(timeoutMs: 5000));

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

                // Process whitelisted machines first - automatically add them to client machine database
                int whitelistedAdded = 0;
                foreach (var machine in newMachines)
                {
                    if (whitelistedIPs.Contains(machine.IPAddress))
                    {
                        try
                        {
                            // Add to database and collect metrics
                            _db.ClientMachines.Add(machine);
                            await _db.SaveChangesAsync();

                            var data = await Task.Run(() => _collector.CollectFull(machine));
                            _monitoringService.UpdateAndSaveMetrics(machine, data);

                            // Update UI
                            _machines.Add(machine);
                            whitelistedAdded++;
                        }
                        catch
                        {
                            // Skip failed whitelisted machines but continue processing
                        }
                    }
                }

                // Add new machines to the detected list, excluding blacklisted and whitelisted machines
                foreach (var machine in newMachines)
                {
                    if (!blacklistedIPs.Contains(machine.IPAddress) && !whitelistedIPs.Contains(machine.IPAddress))
                    {
                        _detectedMachines.Add(machine);
                    }
                }

                // Show appropriate message
                if (whitelistedAdded > 0 && _detectedMachines.Count == 0)
                {
                    System.Windows.MessageBox.Show($"Automatically added {whitelistedAdded} whitelisted machine(s) to monitoring. No new machines detected.", "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (whitelistedAdded > 0)
                {
                    System.Windows.MessageBox.Show($"Automatically added {whitelistedAdded} whitelisted machine(s) to monitoring.", "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (_detectedMachines.Count == 0)
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
                    // Check if machine exists in ClientMachines and remove it
                    var existingMachine = _db.ClientMachines.FirstOrDefault(m => m.IPAddress == selectedMachine.IPAddress);
                    if (existingMachine != null)
                    {
                        _db.ClientMachines.Remove(existingMachine);
                        
                        // Remove from UI collection
                        var machineInCollection = _machines.FirstOrDefault(m => m.IPAddress == selectedMachine.IPAddress);
                        if (machineInCollection != null)
                        {
                            _machines.Remove(machineInCollection);
                        }
                    }

                    // Check if IP exists in whitelist and remove it
                    var existingWhitelist = _db.WhiteLists.FirstOrDefault(w => w.IPAddress == selectedMachine.IPAddress);
                    if (existingWhitelist != null)
                    {
                        _db.WhiteLists.Remove(existingWhitelist);
                        
                        // Remove from UI collection
                        var whitelistInCollection = _whiteList.FirstOrDefault(w => w.IPAddress == selectedMachine.IPAddress);
                        if (whitelistInCollection != null)
                        {
                            _whiteList.Remove(whitelistInCollection);
                        }
                    }

                    // Add to blacklist
                    var blackListEntry = new BlackList
                    {
                        IPAddress = selectedMachine.IPAddress,
                        IsBlocked = true
                    };
                    _db.BlackLists.Add(blackListEntry);

                    await _db.SaveChangesAsync();

                    // Update blacklist UI collection
                    _blackList.Add(blackListEntry);

                    // Remove from detected machines
                    _detectedMachines.Remove(selectedMachine);

                    System.Windows.MessageBox.Show($"Machine {selectedMachine.Name} ({selectedMachine.IPAddress}) has been blacklisted and removed from monitoring.", "Machine Blacklisted", MessageBoxButton.OK, MessageBoxImage.Information);
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
                            // Skip if blacklisted
                            if (_db.BlackLists.Any(b => b.IPAddress == ipAddress))
                                continue;

                            // Skip if already whitelisted
                            if (_db.WhiteLists.Any(w => w.IPAddress == ipAddress))
                                continue;

                            // Add to whitelist directly - don't try to detect or add as client machine
                            var newWhitelistEntry = new WhiteList { IPAddress = ipAddress, IsAllowed = true };
                            _db.WhiteLists.Add(newWhitelistEntry);
                            await _db.SaveChangesAsync();

                            // Update UI - Add to the ObservableCollection
                            _whiteList.Add(newWhitelistEntry);
                            successCount++;
                        }
                        catch { /* Skip failed IPs */ }
                    }

                    System.Windows.MessageBox.Show($"Added {successCount} of {dialog.IPAddresses.Count} IP address(es) to whitelist.", "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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

                            // Check if machine exists in ClientMachines and remove it
                            var existingMachine = _db.ClientMachines.FirstOrDefault(m => m.IPAddress == ipAddress);
                            if (existingMachine != null)
                            {
                                _db.ClientMachines.Remove(existingMachine);
                                
                                // Remove from UI collection
                                var machineInCollection = _machines.FirstOrDefault(m => m.IPAddress == ipAddress);
                                if (machineInCollection != null)
                                {
                                    _machines.Remove(machineInCollection);
                                }
                            }

                            // Check if IP exists in whitelist and remove it
                            var existingWhitelist = _db.WhiteLists.FirstOrDefault(w => w.IPAddress == ipAddress);
                            if (existingWhitelist != null)
                            {
                                _db.WhiteLists.Remove(existingWhitelist);
                                
                                // Remove from UI collection
                                var whitelistInCollection = _whiteList.FirstOrDefault(w => w.IPAddress == ipAddress);
                                if (whitelistInCollection != null)
                                {
                                    _whiteList.Remove(whitelistInCollection);
                                }
                            }

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

                    System.Windows.MessageBox.Show($"Added {successCount} of {dialog.IPAddresses.Count} IP address(es) to blacklist. Any monitored machines and whitelist entries were removed.", "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void HistoryCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            // Refresh plot when calendar selection changes
            ApplyDateFilterAndUpdatePlot();
        }

        private void BtnClearDates_Click(object sender, RoutedEventArgs e)
        {
            // Clear calendar selection to show all time
            HistoryCalendar.SelectedDates.Clear();
        }

        private void ApplyDateFilterAndUpdatePlot()
        {
            if (_machineHistory == null || _machineHistory.Count == 0)
            {
                UpdateScottPlot(new ObservableCollection<MachineHistory>());
                return;
            }

            var filteredHistory = _machineHistory.AsEnumerable();

            // If calendar has selected dates, filter to those dates only
            if (HistoryCalendar.SelectedDates.Count > 0)
            {
                var selectedDates = HistoryCalendar.SelectedDates.OrderBy(d => d).ToList();
                var minDate = selectedDates.First().Date;
                var maxDate = selectedDates.Last().Date.AddDays(1).AddTicks(-1); // End of last selected day

                filteredHistory = filteredHistory.Where(h => 
                    h.TimeStamp.HasValue && 
                    h.TimeStamp.Value >= minDate && 
                    h.TimeStamp.Value <= maxDate);
            }
            // Otherwise, show all time (no filter)

            UpdateScottPlot(new ObservableCollection<MachineHistory>(filteredHistory.ToList()));
        }

        private void UpdateScottPlot(ObservableCollection<MachineHistory> history)
        {
            if (history == null || history.Count == 0)
            {
                WpfPlot1.Plot.Clear();
                WpfPlot1.Plot.Title("No data available");
                WpfPlot1.Refresh();
                
                // Clear statistics
                ClearStatistics();
                return;
            }

            // Clear existing plots
            WpfPlot1.Plot.Clear();

            // Sort by timestamp
            var sortedHistory = history.OrderBy(h => h.TimeStamp ?? DateTime.MinValue).ToList();

            // Prepare data arrays
            var timestamps = sortedHistory.Select(h => h.TimeStamp?.ToOADate() ?? 0).ToArray();
            var cpuData = sortedHistory.Select(h => h.CpuUtilization ?? 0).ToArray();
            var ramData = sortedHistory.Select(h => h.RamUtilization ?? 0).ToArray();
            var gpuData = sortedHistory.Select(h => h.GpuUtilization ?? 0).ToArray();

            // Calculate and update statistics
            UpdateStatistics(cpuData, ramData, gpuData);

            // Add line plots for each metric
            var cpuLine = WpfPlot1.Plot.Add.Scatter(timestamps, cpuData);
            cpuLine.Label = "CPU %";
            cpuLine.LineWidth = 2;
            cpuLine.Color = ScottPlot.Color.FromHex("#FF6B6B"); // Red

            var ramLine = WpfPlot1.Plot.Add.Scatter(timestamps, ramData);
            ramLine.Label = "RAM %";
            ramLine.LineWidth = 2;
            ramLine.Color = ScottPlot.Color.FromHex("#4ECDC4"); // Teal

            var gpuLine = WpfPlot1.Plot.Add.Scatter(timestamps, gpuData);
            gpuLine.Label = "GPU %";
            gpuLine.LineWidth = 2;
            gpuLine.Color = ScottPlot.Color.FromHex("#95E1D3"); // Light Green

            // Configure axes
            WpfPlot1.Plot.Axes.DateTimeTicksBottom();
            WpfPlot1.Plot.Axes.Left.Label.Text = "Utilization (%)";
            WpfPlot1.Plot.Axes.Bottom.Label.Text = "Time";
            
            // Set Y-axis range from 0 to 100
            WpfPlot1.Plot.Axes.SetLimitsY(0, 100);

            // Add legend
            WpfPlot1.Plot.ShowLegend();

            // Build title with date range
            string titleSuffix = "";

            // Set X-axis limits and interaction based on calendar selection
            if (HistoryCalendar.SelectedDates.Count > 0)
            {
                var selectedDates = HistoryCalendar.SelectedDates.OrderBy(d => d).ToList();
                var minDate = selectedDates.First().Date;
                var maxDate = selectedDates.Last().Date.AddDays(1).AddTicks(-1);
                
                // Lock X-axis to selected date range
                WpfPlot1.Plot.Axes.SetLimitsX(minDate.ToOADate(), maxDate.ToOADate());
                
                if (selectedDates.Count == 1)
                {
                    titleSuffix = $" - {selectedDates.First():MMM dd, yyyy}";
                }
                else
                {
                    titleSuffix = $" - {selectedDates.First():MMM dd} to {selectedDates.Last():MMM dd, yyyy}";
                }
            }
            else
            {
                // Allow free zooming when showing all time
                WpfPlot1.Plot.Axes.AutoScale();
                WpfPlot1.Plot.Axes.SetLimitsY(0, 100); // But keep Y locked
                titleSuffix = " - All Time";
            }

            if (ListBoxHistory.SelectedItem is ClientMachine selectedMachine)
            {
                WpfPlot1.Plot.Title($"{selectedMachine.Name}{titleSuffix}");
            }

            // Refresh the plot
            WpfPlot1.Refresh();
        }

        private void UpdateStatistics(double[] cpuData, double[] ramData, double[] gpuData)
        {
            // CPU Statistics - filter out invalid values
            var validCpuData = cpuData?.Where(x => x >= 0 && x <= 100).ToArray();
            if (validCpuData != null && validCpuData.Length > 0)
            {
                AverageCpuUtilization.Text = $"{validCpuData.Average():F2}%";
                MaxCpuUtilization.Text = $"{validCpuData.Max():F2}%";
                MinCpuUtilization.Text = $"{validCpuData.Min():F2}%";
            }
            else
            {
                AverageCpuUtilization.Text = "N/A";
                MaxCpuUtilization.Text = "N/A";
                MinCpuUtilization.Text = "N/A";
            }

            // RAM Statistics - filter out invalid values
            var validRamData = ramData?.Where(x => x >= 0 && x <= 100).ToArray();
            if (validRamData != null && validRamData.Length > 0)
            {
                AverageRamUtilization.Text = $"{validRamData.Average():F2}%";
                MaxRamUtilization.Text = $"{validRamData.Max():F2}%";
                MinRamUtilization.Text = $"{validRamData.Min():F2}%";
            }
            else
            {
                AverageRamUtilization.Text = "N/A";
                MaxRamUtilization.Text = "N/A";
                MinRamUtilization.Text = "N/A";
            }

            // GPU Statistics - filter out invalid values
            var validGpuData = gpuData?.Where(x => x >= 0 && x <= 100).ToArray();
            if (validGpuData != null && validGpuData.Length > 0)
            {
                AverageGpuUtilization.Text = $"{validGpuData.Average():F2}%";
                MaxGpuUtilization.Text = $"{validGpuData.Max():F2}%";
                MinGpuUtilization.Text = $"{validGpuData.Min():F2}%";
            }
            else
            {
                AverageGpuUtilization.Text = "N/A";
                MaxGpuUtilization.Text = "N/A";
                MinGpuUtilization.Text = "N/A";
            }
        }

        private void ClearStatistics()
        {
            // Clear all statistics TextBlocks
            AverageCpuUtilization.Text = "";
            MaxCpuUtilization.Text = "";
            MinCpuUtilization.Text = "";
            
            AverageRamUtilization.Text = "";
            MaxRamUtilization.Text = "";
            MinRamUtilization.Text = "";
            
            AverageGpuUtilization.Text = "";
            MaxGpuUtilization.Text = "";
            MinGpuUtilization.Text = "";
        }
    }
}