using System.Windows;
using System.Windows.Input;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AgentScheduleWindow : Window
    {
        private readonly AgentScheduleViewModel _viewModel;

        public AgentScheduleWindow(AgentScheduleViewModel viewModel)
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
                    _viewModel.StatusMessage = "Could not read the scheduled tasks: " + ex.Message;
                }
                if (_viewModel.Tasks.Count > 0) TaskList.Focus();
                else NameBox.Focus();
            };
            Closed += (_, _) => _viewModel.Detach();
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            if (await _viewModel.AddAsync()) TaskList.Focus();
            else NameBox.Focus();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (await _viewModel.SaveChangesAsync()) TaskList.Focus();
        }

        private async void RunNow_Click(object sender, RoutedEventArgs e) => await _viewModel.RunNowAsync();

        private async void Toggle_Click(object sender, RoutedEventArgs e) => await _viewModel.ToggleEnabledAsync();

        private void OpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.OpenLastResult()) Close();
        }

        private async void Delete_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync();

        private async void TaskList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            await ConfirmDeleteAsync();
        }

        private async Task ConfirmDeleteAsync()
        {
            if (_viewModel.SelectedTask is not { } row) return;
            var answer = MessageBox.Show(this, $"Delete the scheduled task \"{row.Task.Name}\"? Its earlier results stay in Chat history.",
                "Delete scheduled task", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            await _viewModel.DeleteAsync();
            if (_viewModel.Tasks.Count > 0) TaskList.Focus();
            else NameBox.Focus();
        }

        private void NewForm_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ResetForm();
            NameBox.Focus();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder the scheduled task works in" };
            if (dialog.ShowDialog(this) == true) _viewModel.WorkingFolder = dialog.FolderName;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
