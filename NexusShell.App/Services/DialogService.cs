using Microsoft.Win32;
using NexusShell.App.Interfaces;
using System.Windows;

namespace NexusShell.App.Services
{
    public class DialogService : IDialogService
    {
        public string? ShowOpenFileDialog(string filter)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = filter
            };

            if (openFileDialog.ShowDialog() == true)
            {
                return openFileDialog.FileName;
            }

            return null;
        }

        public string? ShowSaveFileDialog(string defaultFileName, string filter)
        {
            var saveFileDialog = new SaveFileDialog
            {
                FileName = defaultFileName,
                Filter = filter
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                return saveFileDialog.FileName;
            }

            return null;
        }

        public void ShowMessage(string message, string title = "Information")
        {
            MessageBox.Show(message, title);
        }
    }
}
