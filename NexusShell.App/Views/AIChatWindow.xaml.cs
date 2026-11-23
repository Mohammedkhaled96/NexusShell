using NexusShell.App.Models;
using NexusShell.App.Services;
using NexusShell.App.ViewModels;
using AgentAttachment = NexusShell.App.ViewModels.AttachmentItem;
using System;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.Views
{
    public partial class AIChatWindow : Window
    {
        private readonly AIChatViewModel _viewModel;
        private bool _isWebViewInitialized = false;
        private bool _isFirstLoad = true;

        private readonly IScreenReaderAnnouncer? _announcer;

        // Only one chat narrates ordinary steps at a time: the one where the user sent the
        // latest message. Other chats still announce approvals and their final result.
        private static AIChatWindow? _narratingWindow;

        public AIChatWindow(AIChatViewModel viewModel, IScreenReaderAnnouncer? announcer = null)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _announcer = announcer;
            DataContext = _viewModel;

            ChatWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 18, 18);
            InitializeWebView();

            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            _viewModel.SpeakRequested += SpeakAgentStep;
            _viewModel.AgentRunStarted += MakeNarratingWindow;
            Loaded += (s, e) => MessageInput.Focus();
            Closed += (s, e) =>
            {
                _viewModel.SpeakRequested -= SpeakAgentStep;
                _viewModel.AgentRunStarted -= MakeNarratingWindow;
                if (ReferenceEquals(_narratingWindow, this)) _narratingWindow = null;
                _viewModel.Shutdown();
            };
        }

        /// <summary>
        /// Speaks an agent step: directly through NVDA / JAWS when available, otherwise as
        /// a UI Automation notification raised by THIS window (the foreground one, so
        /// screen readers do not ignore it). Processing.All queues every step instead of
        /// letting a newer one replace an older one.
        /// </summary>
        private void MakeNarratingWindow() => _narratingWindow = this;

        private void SpeakAgentStep(string text, bool important)
        {
            // Another chat is the one the user is waiting on: stay quiet except for what matters here.
            if (!important && _narratingWindow is not null && !ReferenceEquals(_narratingWindow, this))
            {
                Serilog.Log.Information("Agent step held back (another chat is narrating): {Step}", text);
                return;
            }

            Serilog.Log.Information("Agent step spoken: {Step}", text);
            try
            {
                if (_announcer?.TryAnnounceDirect(text) == true) return;

                var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
                peer?.RaiseNotificationEvent(
                    AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.All,
                    text,
                    "NexusAgentStep");
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Agent step announcement failed.");
            }
        }

        private async void InitializeWebView()
        {
            try
            {
                // 1. Check if WebView2 runtime is available
                bool isRuntimeInstalled = false;
                try
                {
                    string version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                    isRuntimeInstalled = !string.IsNullOrEmpty(version);
                }
                catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException) { }
                catch (Exception) { }

                // 2. If not installed, handle automatic download and installation
                if (!isRuntimeInstalled)
                {
                    var result = MessageBox.Show(
                        "AI Chat requires the Microsoft WebView2 Runtime to display content.\n\n" +
                        "Would you like to download and install it now automatically? (Requires internet connection)",
                        "Components Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        await InstallWebView2RuntimeAsync();
                        // After installation, the user needs to restart the window
                        MessageBox.Show("Installation started. Please wait a moment for it to complete, then reopen the AI Chat window.", "Installation In Progress", MessageBoxButton.OK, MessageBoxImage.Information);
                        Close();
                        return;
                    }
                    else
                    {
                        Close();
                        return;
                    }
                }

                // 3. Specify a user data folder in AppData to avoid permission issues in Program Files
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string webViewDataPath = System.IO.Path.Combine(appDataPath, "NexusShell", "WebView2");
                System.IO.Directory.CreateDirectory(webViewDataPath);

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, webViewDataPath);
                await ChatWebView.EnsureCoreWebView2Async(env);
                
                _isWebViewInitialized = true;
                
                string baseHtml = @"
                <!DOCTYPE html><html><head>
                <style>
                    body { background-color: #121212; color: #E0E0E0; font-family: 'Segoe UI', sans-serif; padding: 20px; line-height: 1.6; overflow-x: hidden; }
                    h2 { font-size: 1.2em; margin-top: 25px; margin-bottom: 8px; border-bottom: 1px solid #333; padding-bottom: 5px; outline: none; }
                    .user-h { color: #4FC3F7; }
                    .ai-h { color: #81C784; }
                    p { margin: 0 0 10px 10px; white-space: pre-wrap; font-size: 1.05em; outline: none; }
                    .ai-p { font-family: 'Consolas', monospace; color: #CCCCCC; }
                    .copy-btn { 
                        color: #007ACC; cursor: pointer; text-decoration: underline; 
                        font-size: 0.9em; margin-left: 10px; display: inline-block; 
                        margin-bottom: 30px; border: none; background: none; padding: 5px;
                    }
                    .separator { border-bottom: 1px solid #2A2A2A; margin-bottom: 10px; }
                </style>
                <script>
                    function copyText(content) { window.chrome.webview.postMessage(content); }
                    
                    function appendMessage(speaker, content, isUser) {
                        const container = document.getElementById('chat-container');
                        
                        const h2 = document.createElement('h2');
                        h2.className = isUser ? 'user-h' : 'ai-h';
                        h2.innerText = speaker + ':';
                        
                        const p = document.createElement('p');
                        p.className = isUser ? '' : 'ai-p';
                        p.innerText = content;
                        
                        const btn = document.createElement('div');
                        btn.className = 'copy-btn';
                        btn.setAttribute('role', 'button');
                        btn.setAttribute('aria-label', 'Copy this message');
                        btn.innerText = 'Copy Message';
                        btn.onclick = () => copyText(content);
                        
                        const sep = document.createElement('div');
                        sep.className = 'separator';
                        
                        container.appendChild(h2);
                        container.appendChild(p);
                        container.appendChild(btn);
                        container.appendChild(sep);
                        
                        // Scroll to bottom only for new messages
                        document.getElementById('end').scrollIntoView({ behavior: 'smooth' });
                    }

                    function clearChat() {
                        document.getElementById('chat-container').innerHTML = '';
                    }
                </script>
                </head><body>
                    <div id='chat-container'></div>
                    <div id='end'></div>
                </body></html>";

                ChatWebView.NavigateToString(baseHtml);
                
                ChatWebView.WebMessageReceived += (s, e) => {
                    var content = e.TryGetWebMessageAsString();
                    if (!string.IsNullOrEmpty(content)) _viewModel.CopyMessageCommand.Execute(content);
                };

                ChatWebView.NavigationCompleted += (s, e) => {
                    if (_isFirstLoad) {
                        foreach (var msg in _viewModel.Messages) AddMessageViaJs(msg);
                        _isFirstLoad = false;
                    }
                };
            }
            catch (Exception ex) {
                MessageBox.Show("Failed to initialize web view: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task InstallWebView2RuntimeAsync()
        {
            try
            {
                string bootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
                string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");

                using (var client = new System.Net.Http.HttpClient())
                {
                    var data = await client.GetByteArrayAsync(bootstrapperUrl);
                    await System.IO.File.WriteAllBytesAsync(tempPath, data);
                }

                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/install", // Standard install, shows small progress UI
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to download or start installer: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_isWebViewInitialized) return;
            if (e.Action == NotifyCollectionChangedAction.Reset) ChatWebView.ExecuteScriptAsync("clearChat();");
            else if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null) {
                foreach (ChatMessage msg in e.NewItems) AddMessageViaJs(msg);
            }
        }

        private void AddMessageViaJs(ChatMessage msg)
        {
            // JSON string literals are valid JavaScript string literals and escape
            // everything (backslashes in Windows paths, quotes, newlines, '<' ...).
            string speaker = System.Text.Json.JsonSerializer.Serialize(msg.SpeakerName.Replace("\r", "").Replace("\n", " "));
            string content = System.Text.Json.JsonSerializer.Serialize(msg.Content);
            string isUser = msg.IsUser ? "true" : "false";
            ChatWebView.ExecuteScriptAsync($"appendMessage({speaker}, {content}, {isUser})");
        }

        private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) {
                e.Handled = true;
                if (_viewModel.SendMessageCommand.CanExecute(null)) _viewModel.SendMessageCommand.Execute(null);
            }
            // No special handling for Tab/Shift+Tab needed now - will work naturally
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ---------- Agent workspace: folder, attachments, capabilities ----------

        private void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the folder NexusAgent works in",
                InitialDirectory = _viewModel.Workspace.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dialog.ShowDialog(this) == true) _viewModel.Workspace.SetWorkingDirectory(dialog.FolderName);
        }

        private void AttachFiles_Click(object sender, RoutedEventArgs e) => AttachFiles();

        private void AttachFolder_Click(object sender, RoutedEventArgs e) => AttachFolders();

        private void AttachFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Attach files to your next message",
                Multiselect = true,
                CheckFileExists = true,
                InitialDirectory = _viewModel.Workspace.WorkingDirectory ?? string.Empty,
                Filter = "All files|*.*|Code and text|*.cs;*.ps1;*.py;*.js;*.ts;*.json;*.xml;*.yml;*.yaml;*.md;*.txt;*.log;*.csv;*.sql;*.sh;*.cmd;*.bat;*.config;*.ini",
            };
            if (dialog.ShowDialog(this) == true) AnnounceAttached(_viewModel.Workspace.AddAttachments(dialog.FileNames));
        }

        private void AttachFolders()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Attach folders to your next message",
                Multiselect = true,
                InitialDirectory = _viewModel.Workspace.WorkingDirectory ?? string.Empty,
            };
            if (dialog.ShowDialog(this) == true) AnnounceAttached(_viewModel.Workspace.AddAttachments(dialog.FolderNames));
        }

        private void AnnounceAttached(int added)
        {
            string text = added == 0
                ? "Nothing new was attached."
                : $"{added} attached. {_viewModel.Workspace.AttachmentsSummary}.";
            _viewModel.Announce(text);
            MessageInput.Focus();
        }

        private void Capabilities_Click(object sender, RoutedEventArgs e)
        {
            var window = new AgentCapabilitiesWindow(_viewModel.Workspace) { Owner = this };
            if (window.ShowDialog() == true) _viewModel.Workspace.ApplyCapabilities();
        }

        private void AttachmentsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Delete or Key.Back) || AttachmentsList.SelectedItem is not AgentAttachment) return;
            int index = AttachmentsList.SelectedIndex;
            _viewModel.RemoveAttachmentCommand.Execute(AttachmentsList.SelectedItem);
            e.Handled = true;
            if (AttachmentsList.Items.Count > 0)
            {
                AttachmentsList.SelectedIndex = Math.Min(index, AttachmentsList.Items.Count - 1);
                (AttachmentsList.ItemContainerGenerator.ContainerFromIndex(AttachmentsList.SelectedIndex) as UIElement)?.Focus();
            }
            else
            {
                MessageInput.Focus();
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = _viewModel.UseAgent && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!_viewModel.UseAgent || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            AnnounceAttached(_viewModel.Workspace.AddAttachments(paths));
            e.Handled = true;
        }

        /// <summary>Ctrl+O attaches files, Ctrl+Shift+O attaches a folder (agent mode only).</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+N new chat, F2 rename this chat.
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _viewModel.NewChat();
                MessageInput.Focus();
                return;
            }
            if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && _viewModel.ChatsAvailable)
            {
                e.Handled = true;
                RenameChat();
                return;
            }

            if (e.Key != Key.O || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !_viewModel.UseAgent) return;
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) AttachFolders();
            else AttachFiles();
        }

        // ---------- Chat actions ----------

        private void ChatActions_Click(object sender, RoutedEventArgs e)
        {
            if (ChatActionsButton.ContextMenu is not { } menu) return;
            menu.PlacementTarget = ChatActionsButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.NewChat();
            MessageInput.Focus();
        }

        private void RenameChat_Click(object sender, RoutedEventArgs e) => RenameChat();

        private async void RenameChat()
        {
            string? title = TextInputDialog.Ask(this, "Rename chat", "New _name for this chat:", _viewModel.CurrentChatTitle);
            if (title is null) return;
            _viewModel.Announce(await _viewModel.RenameCurrentAsync(title));
        }

        private async void PinChat_Click(object sender, RoutedEventArgs e) => _viewModel.Announce(await _viewModel.TogglePinCurrentAsync());

        private async void DuplicateChat_Click(object sender, RoutedEventArgs e) => _viewModel.Announce(await _viewModel.DuplicateCurrentAsync());

        private async void ExportChat_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.HasSavedChat)
            {
                _viewModel.Announce("Send a message first: this chat is not saved yet.");
                return;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export chat as text",
                FileName = SafeFileName(_viewModel.CurrentChatTitle) + ".txt",
                Filter = "Text file|*.txt|Markdown|*.md|All files|*.*",
            };
            if (dialog.ShowDialog(this) != true) return;
            _viewModel.Announce(await _viewModel.ExportCurrentAsync(dialog.FileName));
        }

        private void OpenChatFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.CurrentChatFolder is not { } folder || !System.IO.Directory.Exists(folder))
            {
                _viewModel.Announce("This chat is not saved yet.");
                return;
            }
            OpenInExplorer(folder);
        }

        private void OpenChatsFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(_viewModel.ChatsFolder);
            OpenInExplorer(_viewModel.ChatsFolder);
        }

        private async void DeleteChat_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.HasSavedChat)
            {
                _viewModel.Announce("This chat is not saved, so there is nothing to delete.");
                return;
            }
            var answer = MessageBox.Show(this,
                $"Delete the chat \"{_viewModel.CurrentChatTitle}\"? It goes to the Recycle Bin, so you can restore it from there.",
                "Delete chat", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _viewModel.Announce(await _viewModel.DeleteCurrentAsync());
            MessageInput.Focus();
        }

        private async void MoveChats_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose where NexusShell saves chats (existing chats are moved there)",
                InitialDirectory = _viewModel.ChatsFolder,
            };
            if (dialog.ShowDialog(this) != true) return;
            _viewModel.Announce(await _viewModel.MoveChatsAsync(dialog.FolderName));
        }

        private void Schedule_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.OpenScheduleCommand.CanExecute(null)) _viewModel.OpenScheduleCommand.Execute(null);
        }

        private static void OpenInExplorer(string folder)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not open {Folder}.", folder);
            }
        }

        private static string SafeFileName(string title)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            string name = new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
            return name.Length == 0 ? "chat" : name.Length > 80 ? name[..80] : name;
        }
    }
}
