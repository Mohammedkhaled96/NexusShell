using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Mcp;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Infrastructure.Mcp;

/// <summary>
/// Connects lazily to MCP servers (stdio processes or remote HTTP servers),
/// keeps one connection per server for the app lifetime, and exposes their
/// tools as governed <see cref="AgentToolDescriptor"/>s.
/// Secrets in env vars / headers are expanded from <c>${env:NAME}</c> only at
/// connect time and never stored.
/// </summary>
internal sealed class McpToolProvider(
    IAgentConfigurationProvider configurationProvider,
    ISecretStore secrets,
    ILoggerFactory loggerFactory) : IMcpToolProvider, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = loggerFactory.CreateLogger<McpToolProvider>();
    private readonly ConcurrentDictionary<string, (string Fingerprint, Lazy<Task<McpConnection>> Connection)> _connections = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<AgentToolDescriptor>> GetToolsAsync(IEnumerable<string> serverSelectors, CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var selectors = serverSelectors.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        bool all = selectors.Contains("*");

        var servers = config.McpServers
            .Where(s => s.Enabled && (all || selectors.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var connections = await Task.WhenAll(servers.Select(GetConnectionAsync)).ConfigureAwait(false);
        return connections.SelectMany(c => c.Tools).ToList();
    }

    public async Task<IReadOnlyList<McpServerStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var statuses = new List<McpServerStatus>();

        foreach (var server in config.McpServers)
        {
            if (!server.Enabled)
            {
                statuses.Add(new McpServerStatus(server.Name, false, 0, "Disabled."));
                continue;
            }

            if (_connections.TryGetValue(server.Name, out var entry) && entry.Connection.IsValueCreated && entry.Connection.Value.IsCompleted)
            {
                var connection = await entry.Connection.Value.ConfigureAwait(false);
                statuses.Add(new McpServerStatus(server.Name, connection.Client is not null, connection.Tools.Count, connection.Error ?? "Connected."));
            }
            else
            {
                var errors = server.Validate();
                statuses.Add(new McpServerStatus(server.Name, false, 0, errors.Count > 0 ? string.Join(" ", errors) : "Configured; connects on first use."));
            }
        }

        return statuses;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, entry) in _connections)
        {
            if (!entry.Connection.IsValueCreated) continue;
            try
            {
                var connection = await entry.Connection.Value.ConfigureAwait(false);
                if (connection.Client is not null) await connection.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while closing an MCP connection.");
            }
        }
        _connections.Clear();
    }

    private async Task<McpConnection> GetConnectionAsync(McpServerDefinition server)
    {
        string fingerprint = JsonSerializer.Serialize(server);

        while (true)
        {
            var entry = _connections.GetOrAdd(server.Name, _ => (fingerprint, new Lazy<Task<McpConnection>>(() => ConnectAsync(server))));

            if (entry.Fingerprint != fingerprint)
            {
                // Definition changed (configuration reloaded): replace the old connection.
                if (_connections.TryUpdate(server.Name, (fingerprint, new Lazy<Task<McpConnection>>(() => ConnectAsync(server))), entry))
                    _ = CloseAsync(entry.Connection);
                continue;
            }

            var connection = await entry.Connection.Value.ConfigureAwait(false);
            if (connection.Client is null)
            {
                // Do not cache failures: allow a retry on the next request.
                _connections.TryRemove(new KeyValuePair<string, (string, Lazy<Task<McpConnection>>)>(server.Name, entry));
            }
            return connection;
        }
    }

    private async Task<McpConnection> ConnectAsync(McpServerDefinition server)
    {
        var errors = server.Validate();
        if (errors.Count > 0)
        {
            foreach (string error in errors) _logger.LogWarning("{McpError}", error);
            return McpConnection.Failed(server, string.Join(" ", errors));
        }

        var missing = new List<SecretReference>();
        string Expand(string value)
        {
            string expanded = SecretTemplate.Expand(value, secrets.GetSecret, out var notFound);
            missing.AddRange(notFound);
            return expanded;
        }

        var environment = server.Environment.ToDictionary(kv => kv.Key, kv => Expand(kv.Value));
        var headers = server.Headers.ToDictionary(kv => kv.Key, kv => Expand(kv.Value));
        var arguments = server.Arguments.Select(Expand).ToList();

        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Select(m => m.Name).Distinct());
            _logger.LogWarning("MCP server {Server} skipped: environment variables not set: {Names}.", server.Name, names);
            return McpConnection.Failed(server, $"Set these environment variables: {names}.");
        }

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        try
        {
            IClientTransport transport = server.Transport == McpTransportKind.Http
                ? new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = server.Name,
                    Endpoint = server.Url!,
                    AdditionalHeaders = headers,
                    ConnectionTimeout = TimeSpan.FromSeconds(30),
                }, loggerFactory)
                : new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command!,
                    Arguments = arguments,
                    WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
                    EnvironmentVariables = environment!,
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    StandardErrorLines = line => _logger.LogDebug("[mcp:{Server}] {Line}", server.Name, line),
                }, loggerFactory);

            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "NexusShell", Version = typeof(McpToolProvider).Assembly.GetName().Version?.ToString() ?? "1.0" },
                    InitializationTimeout = TimeSpan.FromSeconds(45),
                },
                loggerFactory,
                timeout.Token).ConfigureAwait(false);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var descriptors = tools.Select(tool => Describe(server, tool)).ToList();

            _logger.LogInformation("MCP server {Server} connected with {ToolCount} tools.", server.Name, descriptors.Count);
            return new McpConnection(server, client, descriptors, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP server {Server} could not be started.", server.Name);
            return McpConnection.Failed(server, $"Could not connect: {ex.Message}");
        }
    }

    internal static AgentToolDescriptor Describe(McpServerDefinition server, McpClientTool tool)
    {
        var annotations = tool.ProtocolTool.Annotations;
        bool readOnly = annotations?.ReadOnlyHint == true;
        var risk = readOnly ? ToolRisk.ReadOnly
            : annotations?.DestructiveHint == true ? ToolRisk.Destructive
            : annotations?.OpenWorldHint == true ? ToolRisk.Network
            : ToolRisk.Mutating;

        bool autoApproved = server.AutoApproveTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)
                            || (server.TrustReadOnlyHints && readOnly);
        bool needsApproval = server.RequireApproval && !autoApproved;

        return new AgentToolDescriptor(
            tool.WithName(McpToolNaming.Create(server.Name, tool.Name)),
            risk,
            "mcp:" + server.Name,
            ForceApproval: needsApproval,
            SkipApproval: !needsApproval);
    }

    private async Task CloseAsync(Lazy<Task<McpConnection>> lazy)
    {
        if (!lazy.IsValueCreated) return;
        try
        {
            var connection = await lazy.Value.ConfigureAwait(false);
            if (connection.Client is not null) await connection.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while closing a replaced MCP connection.");
        }
    }

    private sealed record McpConnection(McpServerDefinition Server, McpClient? Client, IReadOnlyList<AgentToolDescriptor> Tools, string? Error)
    {
        public static McpConnection Failed(McpServerDefinition server, string error) => new(server, null, [], error);
    }
}
