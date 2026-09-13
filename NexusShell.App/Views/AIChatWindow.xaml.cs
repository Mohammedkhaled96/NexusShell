using NexusShell.App.Models;
using NexusShell.App.ViewModels;
using System;
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

        public AIChatWindow(AIChatViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            ChatWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 18, 18);
            InitializeWebView();

            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            Loaded += (s, e) => MessageInput.Focus();
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
            string speaker = msg.SpeakerName.Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
            string content = msg.Content.Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r");
            string isUser = msg.IsUser ? "true" : "false";
            ChatWebView.ExecuteScriptAsync($"appendMessage('{speaker}', '{content}', {isUser})");
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
    }
}
