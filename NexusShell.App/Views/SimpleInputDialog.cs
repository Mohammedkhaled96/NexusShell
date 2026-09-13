using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NexusShell.App.Views
{
    /// <summary>Minimal code-only WPF input dialog for rename / new-name operations.</summary>
    internal static class SimpleInputDialog
    {
        public static string? Show(string title, string prompt, string defaultValue = "", Window? owner = null)
        {
            string? result = null;

            var textBox = new TextBox
            {
                Text                  = defaultValue,
                Margin                = new Thickness(0, 0, 0, 12),
                Background            = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground            = Brushes.White,
                CaretBrush            = Brushes.White,
                BorderBrush           = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                Padding               = new Thickness(8, 6, 8, 6),
                FontSize              = 13,
                SelectionStart        = 0,
                SelectionLength       = defaultValue.Length
            };

            var ok = new Button
            {
                Content               = "OK",
                IsDefault             = true,
                Width                 = 80,
                Height                = 30,
                Margin                = new Thickness(0, 0, 8, 0),
                Background            = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground            = Brushes.White,
                BorderThickness       = new Thickness(0),
                FontWeight            = FontWeights.Bold
            };

            var cancel = new Button
            {
                Content               = "Cancel",
                IsCancel              = true,
                Width                 = 80,
                Height                = 30,
                Background            = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground            = Brushes.White,
                BorderThickness       = new Thickness(0)
            };

            var buttons = new StackPanel
            {
                Orientation           = Orientation.Horizontal,
                HorizontalAlignment   = HorizontalAlignment.Right
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text       = prompt,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                Margin     = new Thickness(0, 0, 0, 8),
                FontSize   = 13
            });
            root.Children.Add(textBox);
            root.Children.Add(buttons);

            var win = new Window
            {
                Title                       = title,
                Content                     = root,
                Width                       = 420,
                Height                      = 160,
                MinWidth                    = 350,
                WindowStartupLocation       = WindowStartupLocation.CenterOwner,
                ResizeMode                  = ResizeMode.NoResize,
                WindowStyle                 = WindowStyle.ToolWindow,
                Background                  = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                ShowInTaskbar               = false,
                Owner                       = owner
            };

            ok.Click += (_, _) =>
            {
                result = textBox.Text.Trim();
                win.DialogResult = true;
            };

            win.Loaded += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            win.ShowDialog();
            return result;
        }
    }
}
