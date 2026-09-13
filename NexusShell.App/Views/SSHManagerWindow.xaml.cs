using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class SSHManagerWindow : Window
    {
        public SSHManagerWindow(SSHManagerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
