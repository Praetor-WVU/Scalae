using Microsoft.EntityFrameworkCore;
using Scalae.Data;
using Scalae.Data.Repositories.EF;
using Scalae.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Text;
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
    /// 
    public partial class MainWindow : Window
    {
        private Database_Context _db = new();
        private ObservableCollection<ClientMachine> _machines;


        public MainWindow()
        {
            InitializeComponent();
            // Ensure DB and tables exist
            _db.Database.EnsureCreated();

            // Load data into an ObservableCollection so the UI updates when items change
            var list = _db.ClientMachines.AsNoTracking().ToList();
            _machines = new ObservableCollection<ClientMachine>(list);

            // Bind the collection to the ListBox
            ListBoxMachines.ItemsSource = _machines;


        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

    }
}