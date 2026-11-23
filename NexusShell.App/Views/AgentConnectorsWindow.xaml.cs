using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AgentConnectorsWindow : Window
    {
        private readonly AgentConnectorsViewModel _viewModel;

        public AgentConnectorsWindow(AgentConnectorsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            Loaded += async (_, _) =>
            {
                try
                {
                    await _viewModel.LoadAsync();
                }
                catch (Exception ex)
                {
                    _viewModel.StatusMessage = "Could not read the connectors: " + ex.Message;
                }
                ConnectorList.Focus();
            };
        }

        // A password box's value is not bindable (it would stay in a view-model string): it is read
        // once here, saved to the user environment and cleared.
        private async void SaveField_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ConnectorFieldRow field, Parent: Grid row }) return;
            string value;
            if (field.IsSecret)
            {
                var box = row.Children.OfType<PasswordBox>().First();
                value = box.Password;
                box.Clear();
            }
            else
            {
                value = field.Text;
            }
            await _viewModel.SaveFieldAsync(field, value);
        }

        private async void Connect_Click(object sender, RoutedEventArgs e) => await _viewModel.ConnectAsync();

        private async void Test_Click(object sender, RoutedEventArgs e) => await _viewModel.TestAsync();

        private async void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Selected is not { } row) return;
            var answer = MessageBox.Show(this, $"Disconnect {row.Name}? The agent loses its tools and the sign-in is removed from your environment.",
                "Disconnect", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) await _viewModel.DisconnectAsync();
        }

        private async void AddMcp_Click(object sender, RoutedEventArgs e) => await _viewModel.AddMcpAsync();

        private async void RemoveMcp_Click(object sender, RoutedEventArgs e) => await _viewModel.RemoveMcpAsync();

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url }) OpenInBrowser(url);
        }

        private async void ImportClientFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the client file you downloaded from Google",
                Filter = "Google client file|client_secret*.json|JSON files|*.json",
                InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) == true) await _viewModel.ImportClientFileAsync(dialog.FileName);
        }

        private void OpenSetup_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Selected?.SetupUrl is { Length: > 0 } url) OpenInBrowser(url);
        }

        private void OpenInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                _viewModel.StatusMessage = "The setup page opened in your browser.";
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = "Could not open the browser: " + ex.Message + " The address is " + url;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
