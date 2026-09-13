namespace NexusShell.App.Models
{
    /// <summary>
    /// Represents a single selectable option in the Interactive Screen overlay.
    /// </summary>
    public class InteractiveOption
    {
        /// <summary>Unique identifier for this option.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Display text shown in the list.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Additional description read aloud by screen readers alongside the text.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Single-character shortcut key (e.g. "1", "A", "F1") that immediately
        /// activates this option.  Leave empty for no shortcut.
        /// </summary>
        public string ShortcutKey { get; set; } = string.Empty;

        /// <summary>Whether this option is currently ticked (multi-select mode).</summary>
        public bool IsSelected { get; set; }

        /// <summary>Whether the option can be interacted with.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Arbitrary caller data attached to this option.</summary>
        public object? Tag { get; set; }

        /// <summary>
        /// A clean formatted label for screen readers.
        /// </summary>
        public string AccessibilityLabel
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(ShortcutKey))
                    parts.Add($"Shortcut: {ShortcutKey}");
                if (!string.IsNullOrWhiteSpace(Text))
                    parts.Add(Text);
                if (!string.IsNullOrWhiteSpace(Description))
                    parts.Add(Description);
                return string.Join(". ", parts);
            }
        }
    }
}
