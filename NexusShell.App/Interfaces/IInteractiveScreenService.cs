using NexusShell.App.Models;

namespace NexusShell.App.Interfaces
{
    /// <summary>
    /// Shows a fully accessible, keyboard-navigable Interactive Screen overlay
    /// inside the main terminal window.  The overlay is displayed above the
    /// terminal output area without interrupting the underlying ConPTY session.
    /// </summary>
    public interface IInteractiveScreenService
    {
        /// <summary>True while an Interactive Screen is currently shown.</summary>
        bool IsActive { get; }

        /// <summary>
        /// Raised whenever <see cref="IsActive"/> changes.
        /// Argument is <c>true</c> when the screen opens, <c>false</c> when it closes.
        /// </summary>
        event EventHandler<bool>? ActiveStateChanged;

        /// <summary>
        /// Displays the Interactive Screen described by <paramref name="config"/>
        /// and asynchronously waits until the user confirms, cancels, or triggers
        /// a custom shortcut.
        /// </summary>
        /// <param name="config">Screen content and behaviour settings.</param>
        /// <returns>
        /// An <see cref="InteractiveScreenResult"/> describing what the user did.
        /// </returns>
        Task<InteractiveScreenResult> ShowAsync(InteractiveScreenConfig config);

        /// <summary>
        /// Raised when a new interactive prompt needs to be displayed in the web view.
        /// </summary>
        event Action<InteractiveScreenConfig>? ShowPromptRequested;

        /// <summary>
        /// Resolves the current active interactive prompt with the JSON result from the web view.
        /// </summary>
        void ResolvePrompt(string jsonResult);

        /// <summary>
        /// Programmatically dismisses the active screen (returns Cancelled result).
        /// No-op when <see cref="IsActive"/> is false.
        /// </summary>
        void Dismiss();
    }
}
