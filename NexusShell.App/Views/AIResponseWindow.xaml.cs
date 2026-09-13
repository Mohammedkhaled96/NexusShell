using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class AIResponseWindow : Window
    {
        private readonly DispatcherTimer _statusClearTimer;

        public AIResponseWindow(AIResponseViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // Focus the text box so screen readers start reading from the top
            Loaded += (s, e) => ResponseTextBox.Focus();

            // Block all text composition — TextBox is strictly read-only
            ResponseTextBox.PreviewTextInput += (s, e) => e.Handled = true;

            // Allow navigation, clipboard, and Tab (for keyboard/screen-reader focus traversal).
            // Block everything else to prevent any text editing.
            ResponseTextBox.PreviewKeyDown += OnResponseTextBoxPreviewKeyDown;

            // Timer to clear the status live-region after announcement
            _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusClearTimer.Tick += (s, e) =>
            {
                StatusText.Text = string.Empty;
                _statusClearTimer.Stop();
            };
        }

        private void OnResponseTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Arrow keys, Page, Home/End — scroll and move caret
            bool isNavigation =
                e.Key == Key.Up       || e.Key == Key.Down  ||
                e.Key == Key.Left     || e.Key == Key.Right ||
                e.Key == Key.PageUp   || e.Key == Key.PageDown ||
                e.Key == Key.Home     || e.Key == Key.End;

            // Ctrl+C (copy selection) and Ctrl+A (select all)
            bool isClipboard =
                (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                (e.Key == Key.C || e.Key == Key.A);

            // Tab / Shift+Tab — move focus to Copy or Close buttons
            bool isTabTraversal = e.Key == Key.Tab;

            if (!isNavigation && !isClipboard && !isTabTraversal)
                e.Handled = true;
        }

        // ── Copy button ─────────────────────────────────────────────────────────

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not AIResponseViewModel vm) return;

            Clipboard.SetText(vm.Response);

            // Update live-region label for screen readers (JAWS / NVDA / Narrator).
            // Clearing first ensures a real text-change event fires even if pressed repeatedly.
            StatusText.Text = string.Empty;
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = "Copied to clipboard.";

                // Raise LiveRegionChanged so screen readers announce the status text immediately.
                var peer = UIElementAutomationPeer.FromElement(StatusText)
                           ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

            }, DispatcherPriority.Background);

            // Clear the visual label after 3 seconds
            _statusClearTimer.Stop();
            _statusClearTimer.Start();
        }

        // ── Close button (also triggered by Escape via IsCancel="True") ─────────

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
