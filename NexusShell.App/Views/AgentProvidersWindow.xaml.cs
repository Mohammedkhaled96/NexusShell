using System.Windows;
using System.Windows.Input;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AgentProvidersWindow : Window
    {
        private readonly AgentProvidersViewModel _viewModel;

        public AgentProvidersWindow(AgentProvidersViewModel viewModel)
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
                ProvidersList.Focus();
            };
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.NewProvider();
            PresetCombo.Focus();
        }

        // PasswordBox.Password is intentionally not bound (it would keep the key in a view-model
        // string); it is read once here and cleared immediately.
        private async void StoreKey_Click(object sender, RoutedEventArgs e)
        {
            string value = KeyValueBox.Password;
            KeyValueBox.Clear();
            if (!await _viewModel.StoreKeyAsync(value)) KeyValueBox.Focus();
        }

        private async void FindModels_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.FindModelsAsync();
            DefaultModelCombo.Focus();
            if (_viewModel.FoundModels.Count > 0) DefaultModelCombo.IsDropDownOpen = true;
        }

        private void AddModel_Click(object sender, RoutedEventArgs e) => _viewModel.AddModel();

        private void RemoveModel_Click(object sender, RoutedEventArgs e) => _viewModel.RemoveModel();

        private void ModelsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            _viewModel.RemoveModel();
            e.Handled = true;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (await _viewModel.SaveAsync()) ProvidersList.Focus();
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                $"Delete the provider {_viewModel.ProviderId}? Built-in providers are turned off instead. Its key stays in your environment.",
                "Delete provider",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm == MessageBoxResult.Yes) await _viewModel.RemoveAsync();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
