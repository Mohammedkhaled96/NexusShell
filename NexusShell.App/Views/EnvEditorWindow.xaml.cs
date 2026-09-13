using System.Windows;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class EnvEditorWindow : Window
    {
        public EnvEditorWindow(EnvEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
