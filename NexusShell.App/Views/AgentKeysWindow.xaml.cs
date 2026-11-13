using System;
using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AgentKeysWindow : Window
    {
        private readonly AgentKeysViewModel _viewModel;

        public AgentKeysWindow(AgentKeysViewModel viewModel)
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
                    _viewModel.StatusMessage = "Could not read the agent configuration: " + ex.Message;
                }
                SecretsList.Focus();
            };
        }

        // PasswordBox.Password is intentionally not bindable (it would keep the
        // secret in a view-model string); it is read once here and cleared.
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string value = ValueBox.Password;
            ValueBox.Clear();
            bool saved = await _viewModel.SaveAsync(value);
            if (!saved) ValueBox.Focus();
            else SecretsList.Focus();
        }

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                $"Remove {_viewModel.VariableName} from your user environment variables?",
                "Remove key",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm == MessageBoxResult.Yes) await _viewModel.RemoveAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
