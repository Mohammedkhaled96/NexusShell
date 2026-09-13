using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.ViewModels;
using System.Windows;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Singleton implementation of <see cref="IInteractiveScreenService"/>.
    /// Drives <see cref="InteractiveScreenViewModel"/> — the UI overlay bound
    /// inside <c>MainWindow.xaml</c> — to present a modal-like, fully
    /// keyboard-navigable and screen-reader-friendly choice screen without
    /// blocking or disrupting the underlying ConPTY terminal pipeline.
    /// </summary>
    public sealed class InteractiveScreenService : IInteractiveScreenService
    {
        private readonly InteractiveScreenViewModel _vm;

        // Completion source lives for the duration of one ShowAsync call.
        private TaskCompletionSource<InteractiveScreenResult>? _tcs;

        // Custom shortcut map for the current session.
        private Dictionary<string, Action> _customShortcuts = new();

        // Current session configuration.
        private InteractiveScreenConfig? _currentConfig;

        public InteractiveScreenService(InteractiveScreenViewModel vm)
        {
            _vm = vm;

            // Wire up VM events once at construction time.
            _vm.ConfirmRequested += OnConfirm;
            _vm.CancelRequested  += OnCancel;
        }

        // ── IInteractiveScreenService ──────────────────────────────────────────

        public bool IsActive => _vm.IsVisible;

        public event EventHandler<bool>? ActiveStateChanged;

        public async Task<InteractiveScreenResult> ShowAsync(InteractiveScreenConfig config)
        {
            // Ensure we run on the UI thread.
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                return await Application.Current.Dispatcher.InvokeAsync(
                    () => ShowAsync(config)).Task.Unwrap();
            }

            // Dismiss any previously open screen first.
            if (_tcs != null && !_tcs.Task.IsCompleted)
                _tcs.TrySetResult(InteractiveScreenResult.Cancelled());

            _tcs = new TaskCompletionSource<InteractiveScreenResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _currentConfig = config;
            _customShortcuts = config.CustomShortcuts ?? new();

            // Populate the VM.
            _vm.LoadConfig(config);

            // Play open sound then show the overlay.
            InteractiveAudioPlayer.PlayOpen();
            _vm.IsVisible = true;
            ActiveStateChanged?.Invoke(this, true);
            
            Serilog.Log.Information($"[DEBUG] InteractiveScreenService raising ShowPromptRequested event for: {config.Title}");
            ShowPromptRequested?.Invoke(config);
            Serilog.Log.Information("[DEBUG] InteractiveScreenService ShowPromptRequested event raised.");

            // Await the user's decision.
            return await _tcs.Task;
        }

        public event Action<InteractiveScreenConfig>? ShowPromptRequested;

        public void ResolvePrompt(string jsonResult)
        {
            Serilog.Log.Information($"[DEBUG] InteractiveScreenService.ResolvePrompt called with: {jsonResult}");
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => ResolvePrompt(jsonResult));
                return;
            }

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(jsonResult))
                {
                    var root = doc.RootElement;
                    bool cancel = root.TryGetProperty("cancel", out var cancelProp) && cancelProp.GetBoolean();
                    
                    if (cancel)
                    {
                        CloseWith(InteractiveScreenResult.Cancelled());
                    }
                    else if (root.TryGetProperty("indices", out var indicesProp))
                    {
                        var selectedOptions = new List<InteractiveOption>();
                        foreach (var el in indicesProp.EnumerateArray())
                        {
                            int idx = el.GetInt32();
                            if (idx >= 0 && idx < _vm.Options.Count)
                                selectedOptions.Add(_vm.Options[idx]);
                        }
                        CloseWith(InteractiveScreenResult.Multi(selectedOptions));
                    }
                    else if (root.TryGetProperty("index", out var indexProp))
                    {
                        int index = indexProp.GetInt32();
                        if (index >= 0 && index < _vm.Options.Count)
                        {
                            var opt = _vm.Options[index];
                            CloseWith(InteractiveScreenResult.Single(opt));
                        }
                        else
                        {
                            CloseWith(InteractiveScreenResult.Cancelled());
                        }
                    }
                    else
                    {
                        CloseWith(InteractiveScreenResult.Cancelled());
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to resolve interactive prompt from JSON: " + jsonResult);
                CloseWith(InteractiveScreenResult.Cancelled());
            }
        }

        public void Dismiss()
        {
            if (!IsActive) return;
            Application.Current.Dispatcher.Invoke(CloseWith, InteractiveScreenResult.Cancelled());
        }

        // ── Internal keyboard routing ──────────────────────────────────────────

        /// <summary>
        /// Called by <c>MainWindow.xaml.cs PreviewKeyDown</c> when the overlay
        /// is active.  Returns <c>true</c> when the key was consumed.
        /// </summary>
        public bool HandleKey(System.Windows.Input.Key key, string keyString)
        {
            if (!_vm.IsVisible) return false;

            if (_currentConfig?.KeyPressedHandler != null)
            {
                if (_currentConfig.KeyPressedHandler(key, keyString))
                {
                    return true;
                }
            }

            switch (key)
            {
                case System.Windows.Input.Key.Up:
                    _vm.MoveUp();
                    return true;

                case System.Windows.Input.Key.Down:
                    _vm.MoveDown();
                    return true;

                case System.Windows.Input.Key.Enter:
                    OnConfirm();
                    return true;

                case System.Windows.Input.Key.Escape:
                    OnCancel();
                    return true;

                case System.Windows.Input.Key.Space:
                    _vm.ToggleCurrentSelection();
                    return true;

                default:
                    // Shortcut key matching (e.g. "1", "A", "F5")
                    if (!string.IsNullOrEmpty(keyString))
                    {
                        // Try option shortcut first
                        var match = _vm.Options.FirstOrDefault(o =>
                            string.Equals(o.ShortcutKey, keyString,
                                         StringComparison.OrdinalIgnoreCase)
                            && o.IsEnabled);

                        if (match != null)
                        {
                            // Select it, then confirm immediately (single-select)
                            // or toggle (multi-select).
                            _vm.SelectedIndex = _vm.Options.IndexOf(match);
                            if (_vm.IsMultiSelect)
                                _vm.ToggleCurrentSelection();
                            else
                                OnConfirm();
                            return true;
                        }

                        // Try custom platform shortcut
                        if (_customShortcuts.TryGetValue(keyString, out var customAction))
                        {
                            customAction.Invoke();
                            CloseWith(InteractiveScreenResult.Custom(keyString));
                            return true;
                        }
                    }
                    return false;
            }
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private void OnConfirm()
        {
            if (!_vm.IsVisible) return;

            InteractiveScreenResult result;
            if (_vm.IsMultiSelect)
            {
                result = InteractiveScreenResult.Multi(_vm.CheckedOptions);
            }
            else
            {
                var opt = _vm.CurrentOption;
                result = opt != null
                    ? InteractiveScreenResult.Single(opt)
                    : InteractiveScreenResult.Cancelled();
            }

            CloseWith(result);
        }

        private void OnCancel()
        {
            if (!_vm.IsVisible) return;
            CloseWith(InteractiveScreenResult.Cancelled());
        }

        private void CloseWith(InteractiveScreenResult result)
        {
            _vm.IsVisible = false;
            _customShortcuts = new();
            _currentConfig = null;
            ActiveStateChanged?.Invoke(this, false);
            _tcs?.TrySetResult(result);
        }
    }
}
