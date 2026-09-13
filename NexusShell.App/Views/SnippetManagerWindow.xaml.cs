using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class SnippetManagerWindow : Window
    {
        public SnippetManagerWindow(SnippetManagerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
