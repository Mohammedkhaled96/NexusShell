using System.Diagnostics;
using System.IO;
using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AgentToolsWindow : Window
    {
        private readonly AgentToolsViewModel _viewModel;

        public AgentToolsWindow(AgentToolsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            Loaded += (_, _) =>
            {
                try
                {
                    _viewModel.Load(reread: true);
                }
                catch (Exception ex)
                {
                    _viewModel.StatusMessage = "Could not read the tools: " + ex.Message;
                }
                ToolList.Focus();
            };
        }

        private void UseWithFile_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Selected is not { } row) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Choose the file for {row.Title}",
                Filter = row.FileFilter,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            _viewModel.UseWith(dialog.FileName);
            DialogResult = true;
        }

        private async void SetUp_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Selected is not { NeedsSetup: true } row) return;
            var answer = MessageBox.Show(this,
                $"Set up {row.Title}?\n\nNexusShell downloads {(row.SetupSize.Length > 0 ? row.SetupSize : "what it needs")} from the libraries' official sources, once. " +
                "It is kept in your user folder; Remove downloads frees the space again.",
                "Set up a tool", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) await _viewModel.SetUpAsync();
        }

        private void CancelSetup_Click(object sender, RoutedEventArgs e) => _viewModel.CancelSetup();

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Selected is not { IsReady: true } row) return;
            var answer = MessageBox.Show(this,
                $"Remove what {row.Title} downloaded? Your results stay. You can set the tool up again later.",
                "Remove downloads", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) await _viewModel.RemoveAsync();
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Selected is not { } row) return;
            try
            {
                Directory.CreateDirectory(row.OutputFolder);
                Process.Start(new ProcessStartInfo(row.OutputFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = "Could not open the folder: " + ex.Message;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
