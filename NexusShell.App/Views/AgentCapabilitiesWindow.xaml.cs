using System.Windows;
using System.Windows.Controls;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Views
{
    /// <summary>
    /// Turns tools, skills and MCP servers on or off for the agent. Works on the workspace's
    /// items directly; Cancel (or closing the window) restores the previous state.
    /// </summary>
    public partial class AgentCapabilitiesWindow : Window
    {
        private readonly AgentWorkspaceViewModel _workspace;
        private readonly Dictionary<CapabilityItem, bool> _snapshot;
        private bool _accepted;

        public AgentCapabilitiesWindow(AgentWorkspaceViewModel workspace)
        {
            InitializeComponent();
            _workspace = workspace;
            DataContext = workspace;
            _snapshot = All().ToDictionary(i => i, i => i.IsEnabled);

            NoTools.Visibility = workspace.Tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoSkills.Visibility = workspace.Skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoMcp.Visibility = workspace.McpServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            Loaded += (_, _) => ToolsTab.Focus();
            Closing += (_, _) =>
            {
                if (_accepted) return;
                foreach (var (item, enabled) in _snapshot) item.IsEnabled = enabled;
            };
        }

        private IEnumerable<CapabilityItem> All() => _workspace.Tools.Concat(_workspace.Skills).Concat(_workspace.McpServers);

        private IEnumerable<CapabilityItem> Group(object? tag) => (tag as string) switch
        {
            "skills" => _workspace.Skills,
            "mcp" => _workspace.McpServers,
            _ => _workspace.Tools,
        };

        private void AllOn_Click(object sender, RoutedEventArgs e) => SetAll(((Button)sender).Tag, true);

        private void AllOff_Click(object sender, RoutedEventArgs e) => SetAll(((Button)sender).Tag, false);

        private void SetAll(object? tag, bool enabled)
        {
            foreach (var item in Group(tag)) item.IsEnabled = enabled;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _accepted = true;
            DialogResult = true;
        }
    }
}
