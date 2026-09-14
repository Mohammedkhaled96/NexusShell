using NexusShell.App.Interfaces;
using NexusShell.App.Services;
using NexusShell.App.ViewModels;
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace NexusShell.App.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ISoundService _soundService;
        private HwndSource? _source;

        public MainWindow(MainViewModel viewModel, ISoundService soundService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _soundService = soundService;
            DataContext = _viewModel;

            // Visual effects: fade the window in on open, and glow the command box on focus.
            VisualEffects.FadeInWindow(this);
            CommandInput.GotKeyboardFocus  += (s, e) => VisualEffects.Glow(CommandInput, true);
            CommandInput.LostKeyboardFocus += (s, e) => VisualEffects.Glow(CommandInput, false);

            // Keystroke click as the user types into the command box.
            CommandInput.PreviewTextInput += (s, e) => _soundService.Play(AppSound.KeyType);

            this.SizeChanged += MainWindow_SizeChanged;

            // Apply defined shortcuts
            _viewModel.ApplyShortcuts(this);
            
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;

            // Accessibility: speak re-read requests (Insert+B) via UI Automation.
            _viewModel.AnnounceAccessibilityRequested += OnAnnounceAccessibility;
            NexusShell.App.Services.ScreenReaderAnnouncer.AnnouncementRequested += OnAnnounceAccessibility;

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        /// <summary>
        /// Handles an accessibility announcement via UI Automation Notification event (freeze-safe, 0 blank artifacts).
        /// </summary>
        private void OnAnnounceAccessibility(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_viewModel.IsInteractiveScreenActive) return;

                    var peer = UIElementAutomationPeer.FromElement(this) 
                               ?? UIElementAutomationPeer.CreatePeerForElement(this);
                    
                    if (peer != null)
                    {
                        peer.RaiseNotificationEvent(
                            AutomationNotificationKind.ActionCompleted,
                            AutomationNotificationProcessing.ImportantMostRecent,
                            text,
                            "Announcement"
                        );
                    }
                    else
                    {
                        ScreenReaderAnnouncer.Text = text;
                        var tbPeer = UIElementAutomationPeer.FromElement(ScreenReaderAnnouncer)
                                   ?? UIElementAutomationPeer.CreatePeerForElement(ScreenReaderAnnouncer);
                        tbPeer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "ReadScreen announcement failed.");
                }
            }));
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            _source = HwndSource.FromHwnd(helper.Handle);
            _source.AddHook(HwndHook);

            // Register Quake Hotkey (Ctrl + `)
            NativeMethods.RegisterHotKey(helper.Handle, NativeMethods.QUAKE_HOTKEY_ID, NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_BACKTICK);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _viewModel.AnnounceAccessibilityRequested -= OnAnnounceAccessibility;
            var helper = new WindowInteropHelper(this);
            NativeMethods.UnregisterHotKey(helper.Handle, NativeMethods.QUAKE_HOTKEY_ID);
            _source?.RemoveHook(HwndHook);
            _source = null;
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == NativeMethods.QUAKE_HOTKEY_ID)
            {
                ToggleQuakeVisibility();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ToggleQuakeVisibility()
        {
            if (this.Visibility == Visibility.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.Activate();
                CommandInput.Focus();
            }
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedTab))
            {
                // When tab/profile switches, auto-focus input
                // Use Dispatcher to ensure UI has updated first
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CommandInput.Focus();
                    VisualEffects.CrossFade(TerminalHost); // gentle fade as the active terminal swaps
                }));
            }
            else if (e.PropertyName == nameof(MainViewModel.IsInteractiveScreenActive))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_viewModel.IsInteractiveScreenActive)
                    {
                        CommandInput.IsEnabled = false;
                    }
                    else
                    {
                        CommandInput.IsEnabled = true;
                        CommandInput.Focus();
                    }
                }));
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ── Universal Terminal Input Streaming ───────────────────────────────
            if (e.OriginalSource == CommandInput)
            {
                var tab = _viewModel.SelectedTab;
                if (tab != null && tab.IsCommandRunning)
                {
                    // While an interactive command / AI CLI agent is running:
                    if (e.Key == Key.Enter || e.Key == Key.Return)
                    {
                        e.Handled = true;
                        tab.SendRawInput(new byte[] { 0x0D });
                        CommandInput.Clear();
                        return;
                    }
                    else if (e.Key == Key.Tab)
                    {
                        e.Handled = true;
                        tab.SendRawInput(new byte[] { 0x09 });
                        return;
                    }
                    else if (e.Key == Key.Back)
                    {
                        tab.SendRawInput(new byte[] { 0x08 });
                        return;
                    }
                    else if (e.Key == Key.Space)
                    {
                        tab.SendRawInput(new byte[] { 0x20 });
                        return;
                    }
                    else if (e.Key == Key.Left)
                    {
                        tab.SendRawInput(System.Text.Encoding.ASCII.GetBytes("\x1b[D"));
                        return;
                    }
                    else if (e.Key == Key.Right)
                    {
                        tab.SendRawInput(System.Text.Encoding.ASCII.GetBytes("\x1b[C"));
                        return;
                    }
                    else if (e.Key == Key.Up)
                    {
                        e.Handled = true;
                        tab.SendRawInput(System.Text.Encoding.ASCII.GetBytes("\x1b[A"));
                        return;
                    }
                    else if (e.Key == Key.Down)
                    {
                        e.Handled = true;
                        tab.SendRawInput(System.Text.Encoding.ASCII.GetBytes("\x1b[B"));
                        return;
                    }
                    else if (e.Key == Key.Escape)
                    {
                        e.Handled = true;
                        tab.SendRawInput(new byte[] { 0x1B });
                        CommandInput.Clear();
                        return;
                    }
                    else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        e.Handled = true;
                        tab.SendRawInput(new byte[] { 0x03 });
                        CommandInput.Clear();
                        return;
                    }
                }
                else
                {
                    // ── Idle Shell Prompt with Authentic Dynamic PowerShell Completions ──
                    if (_viewModel.IsSuggestionsOpen && _viewModel.Suggestions.Count > 0)
                    {
                        if (e.Key == Key.Down)
                        {
                            e.Handled = true;
                            int idx = _viewModel.SelectedSuggestion != null ? _viewModel.Suggestions.IndexOf(_viewModel.SelectedSuggestion) : -1;
                            idx = (idx + 1) % _viewModel.Suggestions.Count;
                            var item = _viewModel.Suggestions[idx];
                            _viewModel.SelectedSuggestion = item;
                            _viewModel.ScreenReaderAnnouncer.Announce($"{item.ListItemText}، {idx + 1} من {_viewModel.Suggestions.Count}");
                            SuggestionList.SelectedIndex = idx;
                            SuggestionList.ScrollIntoView(item);
                            return;
                        }
                        else if (e.Key == Key.Up)
                        {
                            e.Handled = true;
                            int idx = _viewModel.SelectedSuggestion != null ? _viewModel.Suggestions.IndexOf(_viewModel.SelectedSuggestion) : 0;
                            idx = (idx - 1 + _viewModel.Suggestions.Count) % _viewModel.Suggestions.Count;
                            var item = _viewModel.Suggestions[idx];
                            _viewModel.SelectedSuggestion = item;
                            _viewModel.ScreenReaderAnnouncer.Announce($"{item.ListItemText}، {idx + 1} من {_viewModel.Suggestions.Count}");
                            SuggestionList.SelectedIndex = idx;
                            SuggestionList.ScrollIntoView(item);
                            return;
                        }
                        else if (e.Key == Key.Tab || e.Key == Key.Enter || e.Key == Key.Return)
                        {
                            e.Handled = true;
                            _viewModel.ApplySelectedSuggestion(_viewModel.SelectedSuggestion);
                            CommandInput.CaretIndex = CommandInput.Text.Length;
                            return;
                        }
                        else if (e.Key == Key.Escape)
                        {
                            e.Handled = true;
                            _viewModel.IsSuggestionsOpen = false;
                            return;
                        }
                    }
                    else
                    {
                        // At the idle shell prompt: Enter sends command on a SINGLE press
                        if (e.Key == Key.Enter || e.Key == Key.Return)
                        {
                            if (_viewModel.SendCommand.CanExecute(null))
                            {
                                e.Handled = true;
                                _viewModel.SendCommand.Execute(null);
                                return;
                            }
                        }
                        else if (e.Key == Key.Tab)
                        {
                            // Trigger completion query explicitly on Tab
                            e.Handled = true;
                            _viewModel.OnCommandInputChanged(CommandInput.Text, CommandInput.CaretIndex);
                            return;
                        }
                        else if (e.Key == Key.Up)
                        {
                            if (_viewModel.PreviousCommand.CanExecute(null))
                            {
                                e.Handled = true;
                                _viewModel.PreviousCommand.Execute(null);
                                CommandInput.CaretIndex = CommandInput.Text.Length;
                                return;
                            }
                        }
                        else if (e.Key == Key.Down)
                        {
                            if (_viewModel.NextCommand.CanExecute(null))
                            {
                                e.Handled = true;
                                _viewModel.NextCommand.Execute(null);
                                CommandInput.CaretIndex = CommandInput.Text.Length;
                                return;
                            }
                        }
                    }
                }
            }

            // F6 to toggle focus between Input and Terminal (Tabs)
            if (e.Key == Key.F6)
            {
                e.Handled = true;
                if (CommandInput.IsFocused)
                {
                    CommandInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                else
                {
                    CommandInput.Focus();
                }
            }
            // Esc to jump to Input if not already there (Global shortcut)
            else if (e.Key == Key.Escape)
            {
                if (!CommandInput.IsFocused)
                {
                    CommandInput.Focus();
                    e.Handled = true;
                }
            }
        }

        private void CommandInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewModel.SelectedTab?.IsCommandRunning == true)
            {
                _viewModel.IsSuggestionsOpen = false;
                return;
            }
            _viewModel.OnCommandInputChanged(CommandInput.Text, CommandInput.CaretIndex);
        }

        private void CommandInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // When an interactive tool / agent is running, stream characters directly to child process
            if (!string.IsNullOrEmpty(e.Text) && _viewModel.SelectedTab?.IsCommandRunning == true)
            {
                _viewModel.SelectedTab.SendRawString(e.Text);
            }
        }

        private void SuggestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Announcement is handled cleanly on Up/Down navigation
        }

        private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_viewModel.SelectedSuggestion != null)
                {
                    _viewModel.ApplySelectedSuggestion(_viewModel.SelectedSuggestion);
                    CommandInput.Focus();
                    CommandInput.CaretIndex = CommandInput.Text.Length;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _viewModel.IsSuggestionsOpen = false;
                CommandInput.Focus();
                CommandInput.CaretIndex = CommandInput.Text.Length;
            }
            else if (e.Key == Key.Up && SuggestionList.SelectedIndex == 0)
            {
                e.Handled = true;
                CommandInput.Focus();
                CommandInput.CaretIndex = CommandInput.Text.Length;
            }
            else if (e.Key != Key.Down && e.Key != Key.Up && e.Key != Key.PageDown && e.Key != Key.PageUp && e.Key != Key.Home && e.Key != Key.End)
            {
                // If user types normal keys, return focus to CommandInput
                CommandInput.Focus();
            }
        }

        private void SuggestionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.SelectedSuggestion != null)
            {
                _viewModel.ApplySelectedSuggestion(_viewModel.SelectedSuggestion);
                CommandInput.Focus();
                CommandInput.CaretIndex = CommandInput.Text.Length;
            }
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // In multi-tab mode, resizing logic is complex as the view is inside a template.
            // For now, we rely on the control's auto-flow.
            // _viewModel.ResizeView(ActualWidth, ActualHeight);
        }
    }
}