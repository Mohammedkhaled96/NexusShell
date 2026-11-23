using System.Windows;
using System.Windows.Input;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AgentMemoryWindow : Window
    {
        private readonly AgentMemoryViewModel _viewModel;

        public AgentMemoryWindow(AgentMemoryViewModel viewModel)
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
                    _viewModel.StatusMessage = "Could not read the memory: " + ex.Message;
                }
                if (_viewModel.Memories.Count > 0) MemoryList.Focus();
                else MemoryTextBox.Focus();
            };
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            if (await _viewModel.AddAsync()) MemoryList.Focus();
            else MemoryTextBox.Focus();
        }

        private async void SaveChanges_Click(object sender, RoutedEventArgs e)
        {
            if (await _viewModel.SaveChangesAsync()) MemoryList.Focus();
            else MemoryTextBox.Focus();
        }

        private async void TogglePin_Click(object sender, RoutedEventArgs e) => await _viewModel.TogglePinAsync();

        private async void Delete_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync();

        private async void MemoryList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            await ConfirmDeleteAsync();
        }

        private async Task ConfirmDeleteAsync()
        {
            if (_viewModel.SelectedMemory is not { } row) return;
            var answer = MessageBox.Show(this, $"Delete this memory?\n\n{row.Entry.Content}", "Delete memory",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            await _viewModel.DeleteAsync();
            if (_viewModel.Memories.Count > 0) MemoryList.Focus();
            else MemoryTextBox.Focus();
        }

        private async void Clear_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(this, "Delete ALL memories? NexusAgent will forget everything it learned about you and your projects. This cannot be undone.",
                "Delete all memories", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) await _viewModel.ClearAsync();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
