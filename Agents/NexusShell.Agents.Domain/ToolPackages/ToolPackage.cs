using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Domain.ToolPackages;

/// <summary>What runs a tool package's scripts.</summary>
public enum ToolRuntimeKind
{
    /// <summary>Python in the package's own environment, created by NexusShell on first use (uv).</summary>
    Python,

    /// <summary>Windows PowerShell: nothing to install.</summary>
    PowerShell,
}

/// <summary>The kind of value a tool parameter takes; it decides the JSON schema and the checks.</summary>
public enum ToolParameterKind
{
    /// <summary>A path to an existing file (optionally limited to some extensions).</summary>
    File,

    /// <summary>A path to an existing folder.</summary>
    Folder,

    Text,

    Boolean,

    Integer,

    /// <summary>One of <see cref="ToolParameter.Choices"/>.</summary>
    Choice,
}

/// <summary>One input of a tool package, as the developer declared it in tool.json.</summary>
public sealed record ToolParameter
{
    public required string Name { get; init; }

    public required ToolParameterKind Kind { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool Required { get; init; }

    /// <summary>Used when the agent leaves the parameter out (as text; converted to the kind).</summary>
    public string? Default { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>For files: the accepted extensions (".mp3"); empty accepts any file.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
}

/// <summary>One step of installing a Python package's libraries (in order).</summary>
/// <param name="Packages">Package specifiers ("torch"), or empty when <paramref name="Requirements"/> is used.</param>
/// <param name="Requirements">A requirements file inside the package folder.</param>
/// <param name="IndexUrl">Where these come from instead of PyPI (e.g. PyTorch's CPU-only wheels).</param>
public sealed record ToolInstallStep(IReadOnlyList<string> Packages, string? Requirements, string? IndexUrl);

/// <summary>
/// A tool a developer ships with NexusShell (or a user adds): a folder with tool.json (what it is,
/// its inputs, how to run and install it), SKILL.md (when and how the agent should use it) and the
/// scripts. NexusShell installs what the scripts need on first use, so the program itself stays small.
/// </summary>
public sealed record ToolPackage
{
    /// <summary>Lower-case id, also the agent's function name after "tool_" (e.g. "transcribe").</summary>
    public required string Name { get; init; }

    /// <summary>What people read ("Transcribe audio and video").</summary>
    public required string Title { get; init; }

    /// <summary>What the tool does and when to use it (the agent reads this).</summary>
    public required string Description { get; init; }

    public string Version { get; init; } = "1.0.0";

    /// <summary>The package folder.</summary>
    public required string Directory { get; init; }

    public ToolRuntimeKind Runtime { get; init; }

    /// <summary>Script that does the work, relative to <see cref="Directory"/>.</summary>
    public required string Entry { get; init; }

    /// <summary>Optional script run once after the libraries are installed (e.g. downloads a model).</summary>
    public string? SetupScript { get; init; }

    /// <summary>Python version for the package's environment (e.g. "3.12").</summary>
    public string PythonVersion { get; init; } = "3.12";

    public IReadOnlyList<ToolInstallStep> InstallSteps { get; init; } = [];

    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];

    /// <summary>Drives approval: ReadOnly tools (read files, write only their own output) run without asking.</summary>
    public ToolRisk Risk { get; init; } = ToolRisk.ReadOnly;

    public int TimeoutMinutes { get; init; } = 30;

    /// <summary>What the one-time setup downloads, for the user ("about 2 GB: ...").</summary>
    public string SetupSize { get; init; } = string.Empty;

    /// <summary>Shipped with NexusShell (false: added by the user in %AppData%\NexusShell\agent\tools).</summary>
    public bool Bundled { get; init; }

    /// <summary>The agent's instructions (SKILL.md in the package folder), when present.</summary>
    public string? SkillFile { get; init; }

    /// <summary>The name of the function the agent calls.</summary>
    public string FunctionName => "tool_" + Name.Replace('-', '_');

    /// <summary>File extensions the first file parameter accepts (for file pickers).</summary>
    public IReadOnlyList<string> FileExtensions =>
        Parameters.FirstOrDefault(p => p.Kind == ToolParameterKind.File)?.Extensions ?? [];
}
