using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexusShell.App.Models
{
    /// <summary>What a flow does — drives grouping and confirmation prompts in the UI.</summary>
    public enum FlowKind { Install, Uninstall, Status }

    /// <summary>The active shell, derived from the selected <see cref="ShellProfile"/>.</summary>
    public enum ShellKind { PowerShell, Cmd, Wsl }

    /// <summary>A prerequisite tool that must be present before a flow runs.</summary>
    public class FlowPrerequisite
    {
        public string ToolName { get; set; } = "";        // e.g. "Node.js"
        public string DetectCommand { get; set; } = "";   // exit 0 ⇒ present (e.g. "node --version")
        public string? InstallFlowId { get; set; }        // flow to offer if missing (optional)
    }

    /// <summary>One step of a flow.</summary>
    public class FlowStep
    {
        public string Title { get; set; } = "";

        /// <summary>Optional. If this command exits 0, the step is already satisfied and is SKIPPED
        /// (prevents conflicts / redundant installs).</summary>
        public string? DetectCommand { get; set; }

        public string? PowerShellCommand { get; set; }
        public string? CmdCommand { get; set; }

        public bool RequiresAdmin { get; set; }
        public bool ContinueOnError { get; set; }
        public string? Note { get; set; }

        /// <summary>The command to run for the given shell (falls back to the other if one is empty).</summary>
        public string? CommandFor(ShellKind shell) =>
            shell == ShellKind.Cmd
                ? (string.IsNullOrWhiteSpace(CmdCommand) ? PowerShellCommand : CmdCommand)
                : (string.IsNullOrWhiteSpace(PowerShellCommand) ? CmdCommand : PowerShellCommand);
    }

    /// <summary>A named, runnable install/uninstall workflow.</summary>
    public class Flow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Tool { get; set; } = "";
        public FlowKind Kind { get; set; } = FlowKind.Install;

        public List<FlowPrerequisite> Prerequisites { get; set; } = new();
        public List<FlowStep> Steps { get; set; } = new();

        /// <summary>Built-in flows are restored if missing and cannot be deleted by the user.</summary>
        public bool IsBuiltIn { get; set; }

        // Convenience for grouping/labels in the UI.
        [JsonIgnore]
        public string KindLabel => Kind switch
        {
            FlowKind.Install   => "Install",
            FlowKind.Uninstall => "Uninstall",
            _                  => "Status"
        };

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{KindLabel} {Tool}" : Name;
    }
}
