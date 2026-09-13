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

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private bool _announceToggle;

        /// <summary>
        /// Handles an accessibility re-read request (Ctrl+Shift+R). When the interactive
        /// overlay is open, re-reads its title by moving keyboard focus to it — the same
        /// reliable path that reads it on open, and freeze-safe. Otherwise announces a short
        /// status through a tiny isolated live region.
        /// </summary>
        private void OnAnnounceAccessibility(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // Interactive overlay open → re-read its title via focus. Freeze-safe:
                    // focusing one TextBlock never walks the large AvalonEdit UIA subtree
                    // (raising UIA events on the focused element/terminal previously hung
                    // the UI thread).
                    if (_viewModel.IsInteractiveScreenActive)
                    {
                        Serilog.Log.Information("ReadScreen: interactive screen is active.");
                        return;
                    }

                    // No overlay → announce the short status through the isolated live region.
                    if (string.IsNullOrWhiteSpace(text)) return;
                    _announceToggle = !_announceToggle;
                    ScreenReaderAnnouncer.Text = _announceToggle ? text : text + "​";
                    var peer = UIElementAutomationPeer.FromElement(ScreenReaderAnnouncer)
                               ?? UIElementAutomationPeer.CreatePeerForElement(ScreenReaderAnnouncer);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
            // ── Command input + suggestion list (unchanged logic) ──────────────
            if (e.OriginalSource == CommandInput)
            {
                if (_viewModel.IsSuggestionsOpen)
                {
                    if (e.Key == Key.Down)
                    {
                        e.Handled = true;
                        var list = (ListBox)this.FindName("SuggestionList");
                        if (list != null && list.Items.Count > 0)
                        {
                            // Move focus to the list for accessibility (Screen Readers read the item)
                            list.Focus();
                            list.SelectedIndex = 0;

                            // Ensure the ItemContainer is focused so NVDA/JAWS reads it
                            var item = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                            item?.Focus();
                        }
                        return;
                    }
                    else if (e.Key == Key.Escape)
                    {
                        e.Handled = true;
                        _viewModel.IsSuggestionsOpen = false;
                        return;
                    }
                }
            }

            // F6 to toggle focus between Input and Terminal (Tabs)
            if (e.Key == Key.F6)
            {
                e.Handled = true;
                if (CommandInput.IsFocused)
                {
                    // Move focus to the first focusable element in the window content
                    CommandInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                else
                {
                    // Move to Input
                    CommandInput.Focus();
                }
            }
            // Esc to jump to Input if not already there (Global shortcut)
            else if (e.Key == Key.Escape)
            {
                if (!CommandInput.IsFocused)
                {
                    // If suggestions are open and focused, do NOT grab focus here
                    // (let SuggestionList_PreviewKeyDown handle it naturally first)
                    // But if we are somewhere else (like terminal), grab it.
                    var list = (ListBox)this.FindName("SuggestionList");
                    if (list == null || !list.IsKeyboardFocusWithin)
                    {
                        CommandInput.Focus();
                        e.Handled = true;
                    }
                }
            }
        }


        private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_viewModel.SelectedSuggestion != null)
                {
                    _viewModel.ApplySuggestion(_viewModel.SelectedSuggestion);
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
        }

        private void SuggestionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.SelectedSuggestion != null)
            {
                _viewModel.ApplySuggestion(_viewModel.SelectedSuggestion);
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