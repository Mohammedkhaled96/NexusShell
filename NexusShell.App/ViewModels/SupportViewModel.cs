using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class SupportViewModel : ViewModelBase
    {
        private string _fullName = string.Empty;
        public string FullName
        {
            get => _fullName;
            set => SetProperty(ref _fullName, value);
        }

        private string _message = string.Empty;
        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }

        public ObservableCollection<string> Attachments { get; } = new ObservableCollection<string>();

        private bool _hasAttachments;
        public bool HasAttachments
        {
            get => _hasAttachments;
            set => SetProperty(ref _hasAttachments, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public ICommand SubmitCommand { get; }
        public ICommand AttachCommand { get; }
        public ICommand ClearAttachmentsCommand { get; }

        public SupportViewModel()
        {
            SubmitCommand = new RelayCommand(ExecuteSubmit);
            AttachCommand = new RelayCommand(ExecuteAttach);
            ClearAttachmentsCommand = new RelayCommand(ExecuteClearAttachments);
        }

        private void ExecuteClearAttachments(object? obj)
        {
            Attachments.Clear();
            HasAttachments = false;
            StatusMessage = "Attachments cleared.";
        }

        private void ExecuteAttach(object? obj)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.DefaultExt = ".png";
            dlg.Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp|All Files|*.*";
            dlg.Multiselect = true;
            dlg.Title = "Select Screenshots";

            if (dlg.ShowDialog() == true)
            {
                int count = 0;
                foreach (var file in dlg.FileNames)
                {
                    if (!Attachments.Contains(file))
                    {
                        Attachments.Add(file);
                        count++;
                    }
                }
                
                HasAttachments = Attachments.Count > 0;

                if (count > 0)
                    StatusMessage = $"Added {count} file(s). Total: {Attachments.Count}";
                else
                    StatusMessage = "No new files added.";
            }
        }

        private void ExecuteSubmit(object? obj)
        {
            if (string.IsNullOrWhiteSpace(FullName) || 
                string.IsNullOrWhiteSpace(Message))
            {
                MessageBox.Show("Please enter your Name and a Message.", 
                                "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string supportEmail = "moh.khaled96@gmail.com,farid5006@gmail.com"; 
                string subject = Uri.EscapeDataString($"Support: {FullName}");
                
                string attachmentNote = "";
                if (Attachments.Count > 0)
                {
                    attachmentNote = "\n\n[USER: Please attach the following files:]\n";
                    foreach (var path in Attachments)
                    {
                        attachmentNote += $"- {Path.GetFileName(path)}\n";
                    }
                }

                string bodyContent = $"Name: {FullName}\n\n" +
                                     $"Message:\n{Message}\n" +
                                     $"{attachmentNote}\n" +
                                     "--------------------------------\n" +
                                     "Sent via NexusShell";

                string body = Uri.EscapeDataString(bodyContent);

                string mailtoLink = $"mailto:{supportEmail}?subject={subject}&body={body}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = mailtoLink,
                    UseShellExecute = true
                });

                StatusMessage = "Draft created.";
                
                string successMsg = "Support request draft created!\n\nPlease click 'Send' in your email client.";
                if (Attachments.Count > 0)
                {
                    successMsg += "\n\nIMPORTANT: Please manually attach these files (listed in your email body):\n";
                    var preview = Attachments.Take(3).Select(Path.GetFileName);
                    successMsg += string.Join("\n", preview);
                    if (Attachments.Count > 3) successMsg += "\n...";
                }

                MessageBox.Show(successMsg, "Next Steps", MessageBoxButton.OK, MessageBoxImage.Information);

                // Close the form after successful submission
                if (obj is Window window)
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Error opening email client.";
                MessageBox.Show($"Could not open email client: {ex.Message}", 
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
