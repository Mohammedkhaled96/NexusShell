using NexusShell.App.Commands;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public enum UpdateResult
    {
        Update,
        RemindLater,
        Cancel,
        DoNotShowAgain
    }

    public class UpdateAvailableViewModel : ViewModelBase
    {
        private string _updateInfoText;
        public string UpdateInfoText
        {
            get => _updateInfoText;
            set => SetProperty(ref _updateInfoText, value);
        }

        private bool _doNotShowAgain;
        public bool DoNotShowAgain
        {
            get => _doNotShowAgain;
            set => SetProperty(ref _doNotShowAgain, value);
        }

        private string _downloadUrl;
        private Action<UpdateResult> _closeAction;

        public ICommand UpdateCommand { get; }
        public ICommand RemindLaterCommand { get; }
        public ICommand CancelCommand { get; }

        public UpdateAvailableViewModel(string updateInfoText, string downloadUrl, Action<UpdateResult> closeAction)
        {
            _updateInfoText = updateInfoText;
            _downloadUrl = downloadUrl;
            _closeAction = closeAction;

            UpdateCommand = new RelayCommand(ExecuteUpdate);
            RemindLaterCommand = new RelayCommand(ExecuteRemindLater);
            CancelCommand = new RelayCommand(ExecuteCancel);
        }

        private void ExecuteUpdate(object? obj)
        {
            // Only ever hand an absolute HTTPS URL to ShellExecute. The download URL
            // comes from the GitHub releases JSON; if that response were tampered with,
            // a file://, UNC, or custom-protocol value would otherwise be launched by
            // the shell with this (elevated) process's privileges.
            if (Uri.TryCreate(_downloadUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                _closeAction?.Invoke(UpdateResult.Update);
            }
            else
            {
                MessageBox.Show(
                    "The update link is missing or is not a secure (https) URL, so it was not opened.",
                    "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExecuteRemindLater(object? obj)
        {
            _closeAction?.Invoke(UpdateResult.RemindLater);
        }

        private void ExecuteCancel(object? obj)
        {
            if (DoNotShowAgain)
            {
                _closeAction?.Invoke(UpdateResult.DoNotShowAgain);
            }
            else
            {
                _closeAction?.Invoke(UpdateResult.Cancel);
            }
        }
    }
}
