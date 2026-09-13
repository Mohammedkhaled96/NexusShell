using System.Windows;

namespace NexusShell.App.Views
{
    public partial class SupportWindow : Window
    {
        public SupportWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => NameBox.Focus();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}