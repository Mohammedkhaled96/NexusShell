using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AddEditEnvWindow : Window
    {
        public AddEditEnvWindow(AddEditEnvViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
