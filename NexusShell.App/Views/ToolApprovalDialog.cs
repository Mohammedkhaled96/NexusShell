using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NexusShell.Agents.Application.Abstractions.Approval;

namespace NexusShell.App.Views
{
    /// <summary>
    /// Accessible human-in-the-loop prompt shown before the agent runs a risky tool.
    /// Focus lands on the read-only details box so screen readers read the request first.
    /// There is deliberately no default button: Enter never approves by accident; Esc denies.
    /// </summary>
    internal static class ToolApprovalDialog
    {
        public static ToolApprovalDecision Show(ToolApprovalRequest request, Window? owner)
        {
            var decision = ToolApprovalDecision.Deny;

            string risk = request.Risk?.ToString() ?? "Unknown";
            string summary = $"{request.AgentName} wants to run the tool \"{request.ToolName}\" (risk: {risk}).";

            var header = new TextBlock
            {
                Text = summary,
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };

            var details = new TextBox
            {
                Text = $"{summary}\n\nArguments:\n{request.ArgumentsPreview}",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                MinHeight = 140,
                MaxHeight = 320,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 14),
            };
            AutomationProperties.SetName(details, "Tool request details");

            Button MakeButton(string text, Color color, ToolApprovalDecision value, Window window)
            {
                var button = new Button
                {
                    Content = text,
                    MinWidth = 150,
                    Height = 36,
                    Margin = new Thickness(0, 0, 10, 0),
                    Padding = new Thickness(10, 0, 10, 0),
                    Background = new SolidColorBrush(color),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontWeight = FontWeights.SemiBold,
                };
                AutomationProperties.SetName(button, text);
                button.Click += (_, _) =>
                {
                    decision = value;
                    window.DialogResult = value != ToolApprovalDecision.Deny;
                };
                return button;
            }

            var root = new StackPanel { Margin = new Thickness(20) };
            var win = new Window
            {
                Title = "NexusAgent: approve tool?",
                Content = root,
                Width = 620,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                ShowInTaskbar = owner is null,
                Topmost = owner is null,
                Owner = owner,
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(MakeButton("_Allow once", Color.FromRgb(0, 122, 204), ToolApprovalDecision.ApproveOnce, win));
            buttons.Children.Add(MakeButton("Allow for this _conversation", Color.FromRgb(0, 95, 160), ToolApprovalDecision.ApproveForConversation, win));
            var deny = MakeButton("_Deny", Color.FromRgb(150, 40, 40), ToolApprovalDecision.Deny, win);
            deny.IsCancel = true;
            buttons.Children.Add(deny);

            root.Children.Add(header);
            root.Children.Add(details);
            root.Children.Add(buttons);

            win.Loaded += (_, _) => details.Focus();
            win.ShowDialog();
            return decision;
        }
    }
}
