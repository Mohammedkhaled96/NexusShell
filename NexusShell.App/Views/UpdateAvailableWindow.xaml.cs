using NexusShell.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.Views
{
    public partial class UpdateAvailableWindow : Window
    {
        public UpdateResult Result { get; private set; } = UpdateResult.Cancel;

        public UpdateAvailableWindow(string updateInfoText, string downloadUrl)
        {
            InitializeComponent();
            DataContext = new UpdateAvailableViewModel(updateInfoText, downloadUrl, (result) => 
            {
                Result = result;
                this.Close();
            });
            Loaded += UpdateAvailableWindow_Loaded;
        }

        private void UpdateAvailableWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Set initial focus to the Update button for accessibility
            UpdateButton.Focus();
        }
    }
}
