namespace NexusShell.Agents.Domain.Connectors;

public enum ConnectorAuthKind
{
    /// <summary>Sign in through the browser (OAuth 2.0 with PKCE); a refresh token is kept.</summary>
    OAuth,

    /// <summary>A personal access token or API key the user creates.</summary>
    Token,
}

public enum ConnectorState
{
    /// <summary>Setting values (client id, token ...) are missing.</summary>
    NeedsSetup,

    /// <summary>Set up, but the user has not signed in yet (OAuth).</summary>
    NeedsSignIn,

    Connected,
}

/// <summary>A value the user provides for a connector, saved as a user environment variable.</summary>
/// <param name="Variable">Environment variable name (never the value).</param>
/// <param name="IsSecret">Typed in a password box and never shown again.</param>
/// <param name="Optional">Can be left empty.</param>
public sealed record ConnectorField(string Variable, string Label, bool IsSecret, string Help, bool Optional = false);

/// <summary>A button in the Connectors window that opens one setup page.</summary>
public sealed record ConnectorLink(string Label, string Url);

/// <summary>A tool the connector gives the agent, as the Connectors window explains it.</summary>
/// <param name="AsksFirst">True when the user must approve every use (sending, creating, deleting).</param>
public sealed record ConnectorAbility(string Description, bool AsksFirst);

/// <summary>
/// An external application NexusAgent can work with (Gmail, Google Calendar, Outlook, GitHub ...).
/// Holds no secret: credentials live in user environment variables named here.
/// </summary>
public sealed record ConnectorDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public ConnectorAuthKind Auth { get; init; }

    /// <summary>Tool categories the agent gets while the connector is connected (e.g. "gmail").</summary>
    public IReadOnlyList<string> ToolCategories { get; init; } = [];

    public IReadOnlyList<ConnectorField> Fields { get; init; } = [];

    /// <summary>Where the user creates the credentials.</summary>
    public string SetupUrl { get; init; } = string.Empty;

    /// <summary>Numbered, screen-reader friendly setup steps.</summary>
    public IReadOnlyList<string> SetupSteps { get; init; } = [];

    public IReadOnlyList<ConnectorAbility> Abilities { get; init; } = [];

    /// <summary>One button per setup page, in order.</summary>
    public IReadOnlyList<ConnectorLink> SetupLinks { get; init; } = [];

    /// <summary>The settings can be imported from a file the provider lets the user download (Google's client file).</summary>
    public bool SupportsClientFile { get; init; }

    /// <summary>For OAuth connectors: the environment variable holding the refresh token after sign-in.</summary>
    public string? TokenVariable { get; init; }
}
