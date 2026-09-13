using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    public partial class FlowsManagerWindow : Window
    {
        public FlowsManagerWindow(FlowsManagerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // Auto-scroll the progress log to the newest line.
            viewModel.Log.CollectionChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(ScrollLogToEnd));

            // Announce status changes to screen readers (small live-region element only — freeze-safe).
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FlowsManagerViewModel.LiveStatus))
                    AnnounceLiveStatus();
            };
        }

        private void ScrollLogToEnd()
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }

        private void AnnounceLiveStatus()
        {
            try
            {
                var peer = UIElementAutomationPeer.FromElement(LiveRegion)
                           ?? UIElementAutomationPeer.CreatePeerForElement(LiveRegion);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            catch
            {
                // UI Automation is non-critical.
            }
        }

        private void FlowsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RunSelected();

        private void FlowsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                RunSelected();
            }
        }

        private void RunSelected()
        {
            if (DataContext is FlowsManagerViewModel vm && vm.RunCommand.CanExecute(null))
                vm.RunCommand.Execute(null);
        }
    }
}
