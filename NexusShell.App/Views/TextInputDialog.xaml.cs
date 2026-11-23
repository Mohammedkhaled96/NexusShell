using System.Windows;
using System.Windows.Automation;

namespace NexusShell.App.Views
{
    /// <summary>Asks for one line of text (rename a chat ...). Enter confirms, Escape cancels.</summary>
    public partial class TextInputDialog : Window
    {
        private TextInputDialog(string title, string prompt, string initial)
        {
            InitializeComponent();
            Title = title;
            PromptLabel.Content = prompt;
            AutomationProperties.SetName(InputBox, prompt.Replace("_", string.Empty));
            InputBox.Text = initial;
            Loaded += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        public string Value => InputBox.Text.Trim();

        /// <summary>The text typed, or null when cancelled or empty.</summary>
        public static string? Ask(Window? owner, string title, string prompt, string initial = "")
        {
            var dialog = new TextInputDialog(title, prompt, initial) { Owner = owner };
            return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
