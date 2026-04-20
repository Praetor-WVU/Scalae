using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Scalae
{
    /// <summary>
    /// Interaction logic for AddWhitelistDialog.xaml
    /// </summary>
    public partial class AddWhitelistDialog : Window
    {
        public List<string> IPAddresses { get; private set; } = new List<string>();

        public AddWhitelistDialog()
        {
            InitializeComponent();
            TxtIPAddresses.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtIPAddresses.Text))
            {
                MessageBox.Show("Please enter at least one IP address.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Parse IP addresses - split by newlines and clean up
            IPAddresses = TxtIPAddresses.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(ip => ip.Trim()).Where(ip => !string.IsNullOrWhiteSpace(ip)).Distinct().ToList();

            if (IPAddresses.Count == 0)
            {
                MessageBox.Show("No valid IP addresses found.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}


