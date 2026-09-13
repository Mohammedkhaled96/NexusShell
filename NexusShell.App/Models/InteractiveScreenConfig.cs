using System;
using System.Windows.Input;

namespace NexusShell.App.Models
{
    /// <summary>
    /// Configuration passed to <see cref="Interfaces.IInteractiveScreenService.ShowAsync"/>
    /// to describe the content and behaviour of one Interactive Screen session.
    /// </summary>
    public class InteractiveScreenConfig
    {
        // ── Content ────────────────────────────────────────────────────────────

        /// <summary>Short heading announced first by screen readers.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Longer description / instructions shown below the title.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Optional platform or context label (e.g. "Antigravity CLI Settings").
        /// Shown in italics under the description when non-empty.
        /// </summary>
        public string PlatformName { get; set; } = string.Empty;

        /// <summary>The list of choices presented to the user.</summary>
        public List<InteractiveOption> Options { get; set; } = new();

        // ── Behaviour ──────────────────────────────────────────────────────────

        /// <summary>
        /// When true, the user can tick multiple options before confirming.
        /// When false (default), selecting an option confirms immediately.
        /// </summary>
        public bool AllowMultiSelect { get; set; }

        /// <summary>Allow the user to dismiss with Escape / Cancel button.</summary>
        public bool AllowCancel { get; set; } = true;

        // ── Button labels ──────────────────────────────────────────────────────

        public string CancelText  { get; set; } = "Cancel  [Esc]";
        public string ConfirmText { get; set; } = "Confirm  [Enter]";

        // ── Extra shortcuts ────────────────────────────────────────────────────

        /// <summary>
        /// Platform-defined keyboard shortcuts beyond the built-in navigation.
        /// Key   = string representation of the key (e.g. "F5", "A", "Ctrl+R").
        /// Value = handler invoked on the UI thread when that key is pressed.
        /// </summary>
        public Dictionary<string, Action> CustomShortcuts { get; set; } = new();

        /// <summary>
        /// The option index that should be selected initially. Defaults to 0.
        /// </summary>
        public int InitialSelectedIndex { get; set; } = 0;

        /// <summary>
        /// A callback invoked on the UI thread when a key is pressed during the session.
        /// Return true to consume the key and prevent default handling, or false to let it fall through.
        /// </summary>
        public Func<Key, string, bool>? KeyPressedHandler { get; set; }
    }
}
