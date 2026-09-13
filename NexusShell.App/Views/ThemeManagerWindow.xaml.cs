using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class ThemeManagerWindow : Window
    {
        public ThemeManagerWindow(ThemeManagerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
