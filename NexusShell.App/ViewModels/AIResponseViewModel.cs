using System;
using System.Windows.Input;
using NexusShell.App.Commands;

namespace NexusShell.App.ViewModels
{
    public class AIResponseViewModel : ViewModelBase
    {
        private string _title = "AI Response";
        public string Title { get => _title; set => SetProperty(ref _title, value); }

        private string _response = string.Empty;
        public string Response { get => _response; set => SetProperty(ref _response, value); }

        public ICommand CopyCommand     { get; }
        public ICommand OpenChatCommand { get; }

        public AIResponseViewModel(string title, string response, Action? openChatAction = null)
        {
            Title    = title;
            Response = response;

            CopyCommand = new RelayCommand(_ =>
            {
                System.Windows.Clipboard.SetText(Response);
            });

            OpenChatCommand = new RelayCommand(_ => openChatAction?.Invoke(),
                                               _ => openChatAction != null);
        }
    }
}
