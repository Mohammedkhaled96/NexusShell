using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Connectors;
using NexusShell.Agents.Infrastructure.Configuration;

namespace NexusShell.Agents.Infrastructure.Connectors;

/// <summary>
/// Adds and removes connector servers in the user's mcp.json. Secrets are written only as
/// "${env:NAME}" references; the entry is shared with every expert and trusts the server's
/// read-only hints (anything else still asks the user).
/// </summary>
internal sealed class UserMcpSettingsStore(NexusAgentPathsOptions paths, ILogger<UserMcpSettingsStore> logger) : IMcpSettingsStore
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string FilePath => Path.Combine(paths.UserDirectory, "mcp.json");

    public async Task<IReadOnlySet<string>> ListServerNamesAsync(CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return (root["mcpServers"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task AddServerAsync(McpConnectorPreset preset, CancellationToken cancellationToken = default)
    {
        var entry = new JsonObject();
        if (preset.Url is not null)
        {
            entry["type"] = "http";
            entry["url"] = preset.Url;
            if (preset.Headers.Count > 0) entry["headers"] = Map(preset.Headers);
        }
        else
        {
            entry["command"] = preset.Command;
            entry["args"] = new JsonArray(preset.Arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
            if (preset.Environment.Count > 0) entry["env"] = Map(preset.Environment);
        }
        entry["allExperts"] = true;
        entry["trustReadOnlyHints"] = true;

        await EditAsync(root =>
        {
            var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
            servers[preset.Id] = entry;
            root["mcpServers"] = servers;
        }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("MCP connector {Server} written to the user mcp.json.", preset.Id);
    }

    public async Task<bool> RemoveServerAsync(string name, CancellationToken cancellationToken = default)
    {
        bool removed = false;
        await EditAsync(root =>
        {
            if (root["mcpServers"] is JsonObject servers) removed = servers.Remove(name);
        }, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public bool CommandExists(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command);
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions.Prepend(string.Empty))
            {
                try
                {
                    if (File.Exists(Path.Combine(folder.Trim('"'), command + extension.ToLowerInvariant()))) return true;
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry
                }
            }
        }
        return false;
    }

    private static JsonObject Map(IReadOnlyDictionary<string, string> values)
    {
        var map = new JsonObject();
        foreach (var (key, value) in values) map[key] = value;
        return map;
    }

    private async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath)) return new JsonObject();
        string text = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject ?? new JsonObject();
    }

    private async Task EditAsync(Action<JsonObject> edit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.UserDirectory);
            if (File.Exists(FilePath) && !File.Exists(FilePath + ".bak"))
                File.Copy(FilePath, FilePath + ".bak"); // the original keeps its comments and examples

            var root = await ReadAsync(cancellationToken).ConfigureAwait(false);
            edit(root);
            string temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
