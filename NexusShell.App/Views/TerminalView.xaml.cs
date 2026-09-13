using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    /// <summary>
    /// Hosts the accessible HTML output renderer (WebTerminal/terminal.html) in a
    /// WebView2. A hidden xterm.js inside the page resolves the raw ConPTY stream
    /// into clean text and renders it as accessible command-blocks — replacing the
    /// old AvalonEdit + filter pipeline. Input remains in the WPF command box.
    /// </summary>
    public partial class TerminalView : UserControl
    {
        // C# → JS (first char): 'o' output, 'c' command-start, 'x' system, 'k' clear, 's' shell.
        // JS → C# (first char): 'R' ready.
        private TerminalTabViewModel? _vm;
        private bool _coreReady;          // CoreWebView2 created
        private bool _pageReady;          // page reported 'R'
        private readonly StringBuilder _pending = new();   // output buffered until the page is ready
        private readonly object _pendingLock = new();

        public TerminalView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_coreReady) return;
            try
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NexusShell", "WebView2");
                Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await WebTerminal.EnsureCoreWebView2Async(env);

                var core = WebTerminal.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;

                string assets = Path.Combine(AppContext.BaseDirectory, "WebTerminal");
                core.SetVirtualHostNameToFolderMapping(
                    "nexus.terminal", assets, CoreWebView2HostResourceAccessKind.Allow);

                core.WebMessageReceived += OnWebMessageReceived;

                _coreReady = true;
                WebTerminal.Source = new Uri("https://nexus.terminal/terminal.html");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex}");
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is TerminalTabViewModel oldVm)
            {
                oldVm.RawOutputReceived -= OnRawOutput;
                oldVm.CommandStarted -= OnCommandStarted;
                oldVm.ClearScreenRequested -= OnClearScreen;
                oldVm.InteractiveScreenService.ShowPromptRequested -= OnShowPromptRequested;
            }

            if (e.NewValue is TerminalTabViewModel vm)
            {
                _vm = vm;
                _pageReady = false;
                lock (_pendingLock) { _pending.Clear(); }

                // Snapshot scrollback BEFORE subscribing so live events arriving during
                // page load are buffered exactly once (no duplication).
                string history = vm.GetRawHistory();
                if (!string.IsNullOrEmpty(history))
                {
                    lock (_pendingLock) { _pending.Append(history); }
                }

                vm.RawOutputReceived += OnRawOutput;
                vm.CommandStarted += OnCommandStarted;
                vm.ClearScreenRequested += OnClearScreen;
                vm.InteractiveScreenService.ShowPromptRequested += OnShowPromptRequested;
            }
            else
            {
                _vm = null;
            }
        }

        // Raw ConPTY output (reader thread) → buffer or forward to the renderer.
        private void OnRawOutput(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            if (!_pageReady)
            {
                lock (_pendingLock) { _pending.Append(data); }
                return;
            }
            Post('o', data);
        }

        private void OnCommandStarted(string command) => Post('c', command ?? string.Empty);

        private void OnClearScreen() => PostRaw("k");

        private void Post(char kind, string body) => PostRaw(kind + body);

        private void PostRaw(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PostRaw(message)));
                return;
            }
            if (!_coreReady || WebTerminal.CoreWebView2 == null) return;
            WebTerminal.CoreWebView2.PostWebMessageAsString(message);
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try { msg = e.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrEmpty(msg)) return;

            if (msg[0] == 'R')   // page ready → flush buffered output
                FlushPending();
            else if (msg[0] == 'i')   // interactive prompt result
            {
                var resultJson = msg.Substring(1);
                _vm?.InteractiveScreenService.ResolvePrompt(resultJson);
            }
            else if (msg[0] == 'd')   // prompt detected by JS
            {
                var promptJson = msg.Substring(1);
                _vm?.HandleJsPromptDetected(promptJson);
            }
        }

        private void OnShowPromptRequested(NexusShell.App.Models.InteractiveScreenConfig config)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnShowPromptRequested(config)));
                return;
            }

            try
            {
                Serilog.Log.Information($"[DEBUG] TerminalView serializing prompt for: {config.Title}");
                var dto = new
                {
                    title = config.Title,
                    description = config.Description,
                    platformName = config.PlatformName,
                    allowMultiSelect = config.AllowMultiSelect,
                    allowCancel = config.AllowCancel,
                    cancelText = config.CancelText,
                    confirmText = config.ConfirmText,
                    initialSelectedIndex = config.InitialSelectedIndex,
                    options = config.Options.Select((o, idx) => new
                    {
                        index = idx,
                        text = o.Text,
                        description = o.Description,
                        shortcutKey = o.ShortcutKey,
                        isSelected = o.IsSelected,
                        isEnabled = o.IsEnabled,
                        value = o.Tag?.ToString() ?? string.Empty
                    }).ToList()
                };

                // Check if YesNo or List or Tui selector.
                // We'll pass the type to help the JS render the correct markup
                string type = "list";
                if (config.Options.Count == 2 && 
                    (config.Options[0].Text.Equals("Yes", StringComparison.OrdinalIgnoreCase) || config.Options[0].Text.Equals("y", StringComparison.OrdinalIgnoreCase)) &&
                    (config.Options[1].Text.Equals("No", StringComparison.OrdinalIgnoreCase) || config.Options[1].Text.Equals("n", StringComparison.OrdinalIgnoreCase)))
                {
                    type = "yesno";
                }
                else if (config.KeyPressedHandler != null || config.CustomShortcuts.Count > 0)
                {
                    // If it is a TUI selection menu (like Arrow Down/Up simulation)
                    type = "tui";
                }

                var finalDto = new
                {
                    type = type,
                    title = dto.title,
                    description = dto.description,
                    platformName = dto.platformName,
                    allowMultiSelect = dto.allowMultiSelect,
                    allowCancel = dto.allowCancel,
                    cancelText = dto.cancelText,
                    confirmText = dto.confirmText,
                    currentIndex = dto.initialSelectedIndex,
                    options = dto.options
                };

                string json = System.Text.Json.JsonSerializer.Serialize(finalDto);
                Serilog.Log.Information($"[DEBUG] TerminalView sending json: {json}");
                PostRaw("i" + json);

                // Focus WebView2 immediately
                WebTerminal.Focus();
                Serilog.Log.Information("[DEBUG] TerminalView focused WebView2.");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to serialize prompt");
            }
        }

        private void FlushPending()
        {
            string buffered;
            lock (_pendingLock)
            {
                buffered = _pending.ToString();
                _pending.Clear();
            }
            _pageReady = true;
            if (buffered.Length > 0)
                Post('o', buffered);
        }
    }
}
