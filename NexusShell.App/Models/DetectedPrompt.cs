using System.Collections.Generic;

namespace NexusShell.App.Models
{
    /// <summary>Type of interactive prompt detected from ConPTY output.</summary>
    public enum PromptType
    {
        /// <summary>A numbered or lettered list of options (e.g. "1. Option A").</summary>
        NumberedList,

        /// <summary>A yes / no confirmation prompt (e.g. "(y/n)").</summary>
        YesNo,

        /// <summary>
        /// A cursor-based TUI menu where one item is marked with '>' and
        /// the program expects raw arrow-key input, not typed text.
        /// </summary>
        TuiSelector
    }

    /// <summary>
    /// Represents an interactive prompt detected in terminal output.
    /// Created by <see cref="Services.InteractivePromptDetector"/> and consumed by
    /// <see cref="ViewModels.TerminalTabViewModel"/> to show the Interactive Screen overlay.
    /// </summary>
    public sealed class DetectedPrompt
    {
        /// <summary>Classification of the prompt.</summary>
        public PromptType Type { get; init; }

        /// <summary>Human-readable title / question text for the overlay header.</summary>
        public string Title { get; init; } = "Select an option";

        /// <summary>
        /// Ordered list of (Value, DisplayText) pairs.
        /// <c>Value</c> is the string sent back to ConPTY stdin (e.g. "1", "y")
        /// or, for <see cref="PromptType.TuiSelector"/>, the 0-based index.
        /// <c>DisplayText</c> is what the user sees in the Interactive Screen.
        /// </summary>
        public List<(string Value, string DisplayText)> Options { get; init; } = new();

        /// <summary>
        /// For <see cref="PromptType.TuiSelector"/>: the 0-based index of the
        /// option currently marked with '>' in the TUI menu.
        /// </summary>
        public int CurrentIndex { get; init; }
    }
}
