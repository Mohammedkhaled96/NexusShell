using System.Collections.Generic;

namespace NexusShell.App.Interfaces
{
    public interface IDialogService
    {
        string? ShowOpenFileDialog(string filter);
        string? ShowSaveFileDialog(string defaultFileName, string filter);
        void ShowMessage(string message, string title = "Information");
    }
}
