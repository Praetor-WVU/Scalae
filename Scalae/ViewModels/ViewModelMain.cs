using Scalae.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Scalae.ViewModels
{
    /// <summary>
    /// Starter MVVM view-model for the main view.
    /// - Keeps an ObservableCollection of machines
    /// - Exposes SelectedMachine and an async Refresh command
    /// - Constructor accepts a loader func so the view-model stays testable / decoupled from DB
    /// </summary>
    public class ViewModelMain : ViewModelBase
    {
        private readonly Func<Task<IEnumerable<ClientMachine>>>? _loader;

        public ObservableCollection<ClientMachine> Machines { get; } = new();

        private ClientMachine? _selectedMachine;
        public ClientMachine? SelectedMachine
        {
            get => _selectedMachine;
            set => SetProperty(ref _selectedMachine, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    // if you have commands that depend on IsBusy, call RaiseCanExecuteChanged on them
                    RefreshCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public AsyncRelayCommand RefreshCommand { get; }

        public ViewModelMain(Func<Task<IEnumerable<ClientMachine>>>? loader = null)
        {
            _loader = loader;
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && _loader != null);
        }

        private async Task RefreshAsync()
        {
            if (_loader == null) return;
            IsBusy = true;
            try
            {
                var items = await _loader().ConfigureAwait(false);

                // update ObservableCollection on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Machines.Clear();
                    foreach (var m in items.OrderBy(x => x.Name ?? x.IPAddress))
                        Machines.Add(m);
                });
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
