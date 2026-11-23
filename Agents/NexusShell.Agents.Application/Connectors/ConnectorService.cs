using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Connectors;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Application.Connectors;

/// <summary>Result of connecting or testing: the state and the account it uses (e.g. an e-mail address).</summary>
public sealed record ConnectorStatus(ConnectorState State, string Detail, string? Account = null);

/// <summary>
/// Port: one external application (implemented in Infrastructure: Google, Microsoft, GitHub ...).
/// Credentials come from user environment variables; nothing secret is kept in files.
/// </summary>
public interface IConnector
{
    ConnectorDefinition Definition { get; }

    /// <summary>Quick check without network: are the needed values and tokens present?</summary>
    ConnectorState CurrentState { get; }

    /// <summary>OAuth: signs in through the browser. Token: checks it. Returns the connected account.</summary>
    Task<ConnectorStatus> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Calls the service to prove the connection works.</summary>
    Task<ConnectorStatus> TestAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the sign-in (refresh token or access token variable).</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the settings from a file the provider gave the user, then signs in.</summary>
    Task<ConnectorStatus> ImportClientFileAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{Definition.Name} has no settings file to import.");
}

/// <summary>A connector as the Connectors window shows it.</summary>
public sealed record ConnectorInfo(ConnectorDefinition Definition, ConnectorState State, IReadOnlyDictionary<string, bool> FieldsSet);

/// <summary>An MCP connector template as the Connectors window shows it.</summary>
/// <param name="Added">Present in the user's mcp.json.</param>
/// <param name="RuntimeFound">The command it needs (npx, uvx) was found, or it needs none.</param>
public sealed record McpConnectorInfo(McpConnectorPreset Preset, bool Added, bool RuntimeFound, IReadOnlyDictionary<string, bool> FieldsSet)
{
    public bool Ready => Added && RuntimeFound && Preset.Fields.Where(f => !f.Optional).All(f => FieldsSet.GetValueOrDefault(f.Variable));
}

/// <summary>Port: the user's mcp.json (add or remove connector servers) and command lookup.</summary>
public interface IMcpSettingsStore
{
    Task<IReadOnlySet<string>> ListServerNamesAsync(CancellationToken cancellationToken = default);

    Task AddServerAsync(McpConnectorPreset preset, CancellationToken cancellationToken = default);

    Task<bool> RemoveServerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>True when the command (e.g. "npx") can be started from PATH.</summary>
    bool CommandExists(string command);
}

/// <summary>
/// Connectors to external applications: native ones (Gmail, Google Calendar, Outlook, GitHub) and
/// MCP templates (Notion, Jira and Confluence, Slack, browser automation ...). Connected native
/// connectors give every expert their tools; reading is free, sending and changing always ask.
/// </summary>
public interface IConnectorService
{
    /// <summary>Something was connected, disconnected or added.</summary>
    event Action? Changed;

    IReadOnlyList<ConnectorInfo> List();

    /// <summary>Tool categories of the connected native connectors (no network; called for every expert).</summary>
    IReadOnlyList<string> ActiveToolCategories();

    /// <summary>Saves a connector value (client id, token ...) as a user environment variable.</summary>
    Task SaveFieldAsync(string connectorId, string variable, string value, CancellationToken cancellationToken = default);

    Task<ConnectorStatus> ConnectAsync(string connectorId, CancellationToken cancellationToken = default);

    Task<ConnectorStatus> TestAsync(string connectorId, CancellationToken cancellationToken = default);

    Task DisconnectAsync(string connectorId, CancellationToken cancellationToken = default);

    /// <summary>Imports the provider's settings file (e.g. Google's client file) and signs in.</summary>
    Task<ConnectorStatus> ImportClientFileAsync(string connectorId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpConnectorInfo>> ListMcpAsync(CancellationToken cancellationToken = default);

    Task SaveMcpFieldAsync(string presetId, string variable, string value, CancellationToken cancellationToken = default);

    Task AddMcpAsync(string presetId, CancellationToken cancellationToken = default);

    Task RemoveMcpAsync(string presetId, CancellationToken cancellationToken = default);
}

internal sealed class ConnectorService(
    IEnumerable<IConnector> connectors,
    IMcpSettingsStore mcpSettings,
    ISecretStore secrets,
    IAgentConfigurationProvider configuration,
    ILogger<ConnectorService> logger) : IConnectorService
{
    private readonly IReadOnlyList<IConnector> _connectors = connectors.ToList();

    public event Action? Changed;

    public IReadOnlyList<ConnectorInfo> List() => _connectors
        .Select(c => new ConnectorInfo(c.Definition, c.CurrentState, FieldsSet(c.Definition.Fields)))
        .ToList();

    public IReadOnlyList<string> ActiveToolCategories() => _connectors
        .Where(c => c.CurrentState == ConnectorState.Connected)
        .SelectMany(c => c.Definition.ToolCategories)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task SaveFieldAsync(string connectorId, string variable, string value, CancellationToken cancellationToken = default)
    {
        var field = Find(connectorId).Definition.Fields.FirstOrDefault(f => f.Variable == variable)
            ?? throw new ArgumentException($"{variable} is not a setting of this connector.");
        await SaveAsync(field, value, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task<ConnectorStatus> ConnectAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        var connector = Find(connectorId);
        var status = await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Connector {Connector}: {State}.", connectorId, status.State);
        Changed?.Invoke();
        return status;
    }

    public Task<ConnectorStatus> TestAsync(string connectorId, CancellationToken cancellationToken = default) =>
        Find(connectorId).TestAsync(cancellationToken);

    public async Task<ConnectorStatus> ImportClientFileAsync(string connectorId, string path, CancellationToken cancellationToken = default)
    {
        var status = await Find(connectorId).ImportClientFileAsync(path, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Connector {Connector}: settings imported from a file, {State}.", connectorId, status.State);
        Changed?.Invoke();
        return status;
    }

    public async Task DisconnectAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        await Find(connectorId).DisconnectAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Connector {Connector} disconnected.", connectorId);
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<McpConnectorInfo>> ListMcpAsync(CancellationToken cancellationToken = default)
    {
        var added = await mcpSettings.ListServerNamesAsync(cancellationToken).ConfigureAwait(false);
        return McpConnectorPresets.All
            .Select(p => new McpConnectorInfo(
                p,
                added.Contains(p.Id),
                p.Command is null || mcpSettings.CommandExists(p.Command),
                FieldsSet(p.Fields)))
            .ToList();
    }

    public async Task SaveMcpFieldAsync(string presetId, string variable, string value, CancellationToken cancellationToken = default)
    {
        var field = FindPreset(presetId).Fields.FirstOrDefault(f => f.Variable == variable)
            ?? throw new ArgumentException($"{variable} is not a setting of this connector.");
        await SaveAsync(field, value, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task AddMcpAsync(string presetId, CancellationToken cancellationToken = default)
    {
        var preset = FindPreset(presetId);
        await mcpSettings.AddServerAsync(preset, cancellationToken).ConfigureAwait(false);
        configuration.Invalidate();
        logger.LogInformation("MCP connector {Preset} added.", presetId);
        Changed?.Invoke();
    }

    public async Task RemoveMcpAsync(string presetId, CancellationToken cancellationToken = default)
    {
        if (await mcpSettings.RemoveServerAsync(presetId, cancellationToken).ConfigureAwait(false))
        {
            configuration.Invalidate();
            logger.LogInformation("MCP connector {Preset} removed.", presetId);
            Changed?.Invoke();
        }
    }

    private async Task SaveAsync(ConnectorField field, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Type the {field.Label} first.");
        string trimmed = value.Trim();
        if (trimmed.Length > 4096 || trimmed.Contains('\n', StringComparison.Ordinal))
            throw new ArgumentException("That value is too long or has several lines; paste only the value.");
        // A non-secret field (URL, e-mail, id) must not receive a credential by mistake.
        if (!field.IsSecret && SecretPatterns.LooksLikeSecret(trimmed))
            throw new ArgumentException($"That looks like a key or token, but {field.Label} is not a secret field. Paste it in the right box.");
        await secrets.SetSecretAsync(new SecretReference(field.Variable), trimmed, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyDictionary<string, bool> FieldsSet(IEnumerable<ConnectorField> fields) =>
        fields.ToDictionary(f => f.Variable, f => secrets.HasSecret(new SecretReference(f.Variable)));

    private IConnector Find(string id) =>
        _connectors.FirstOrDefault(c => string.Equals(c.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There is no connector '{id}'.");

    private static McpConnectorPreset FindPreset(string id) =>
        McpConnectorPresets.All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There is no connector template '{id}'.");
}
