namespace NexusShell.App.Models
{
    /// <summary>
    /// The result returned by <see cref="Interfaces.IInteractiveScreenService.ShowAsync"/>
    /// after the user confirms or cancels the Interactive Screen.
    /// </summary>
    public class InteractiveScreenResult
    {
        /// <summary>True when the user pressed Escape or the Cancel button.</summary>
        public bool WasCancelled { get; init; }

        /// <summary>
        /// The option the user confirmed (single-select mode).
        /// Null when <see cref="WasCancelled"/> is true.
        /// </summary>
        public InteractiveOption? SelectedOption { get; init; }

        /// <summary>
        /// All ticked options (multi-select mode).
        /// Empty when <see cref="WasCancelled"/> is true.
        /// </summary>
        public IReadOnlyList<InteractiveOption> SelectedOptions { get; init; }
            = Array.Empty<InteractiveOption>();

        /// <summary>
        /// Non-null when the session ended because a custom shortcut was triggered.
        /// Contains the shortcut key string registered in
        /// <see cref="InteractiveScreenConfig.CustomShortcuts"/>.
        /// </summary>
        public string? CustomActionKey { get; init; }

        // ── Convenience factories ──────────────────────────────────────────────

        public static InteractiveScreenResult Cancelled()
            => new() { WasCancelled = true };

        public static InteractiveScreenResult Single(InteractiveOption option)
            => new() { SelectedOption = option, SelectedOptions = new[] { option } };

        public static InteractiveScreenResult Multi(IReadOnlyList<InteractiveOption> options)
            => new() { SelectedOptions = options };

        public static InteractiveScreenResult Custom(string key)
            => new() { CustomActionKey = key };
    }
}
