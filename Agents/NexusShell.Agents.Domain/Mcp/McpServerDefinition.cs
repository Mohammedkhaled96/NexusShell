using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Domain.Mcp;

public enum McpTransportKind
{
    /// <summary>Local process speaking MCP over stdin/stdout.</summary>
    Stdio,

    /// <summary>Remote server (Streamable HTTP, with SSE fallback).</summary>
    Http,
}

/// <summary>
/// An MCP server. Secret-bearing values (env vars, headers) must use
/// <c>${env:NAME}</c> environment references; literal credentials are rejected by <see cref="Validate"/>.
/// </summary>
public sealed record McpServerDefinition
{
    public required string Name { get; init; }

    public McpTransportKind Transport { get; init; } = McpTransportKind.Stdio;

    public string? Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public Uri? Url { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public bool Enabled { get; init; } = true;

    /// <summary>Ask the user before any tool of this server runs (default: yes).</summary>
    public bool RequireApproval { get; init; } = true;

    /// <summary>Tools of this server that never need approval.</summary>
    public IReadOnlyList<string> AutoApproveTools { get; init; } = [];

    /// <summary>Skip approval for tools the server marks readOnlyHint=true. Only for servers you trust.</summary>
    public bool TrustReadOnlyHints { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(Command))
            errors.Add($"MCP server '{Name}': 'command' is required for stdio servers.");

        if (Transport == McpTransportKind.Http && Url is null)
            errors.Add($"MCP server '{Name}': 'url' is required for HTTP servers.");

        if (Url is not null && Url.Scheme != Uri.UriSchemeHttps && !Url.IsLoopback)
            errors.Add($"MCP server '{Name}': remote servers must use https.");

        foreach (var (key, value) in Environment)
        {
            if (SecretTemplate.IsLiteralSecret(key, value))
                errors.Add($"MCP server '{Name}': env '{key}' contains a literal secret. Use ${{env:VARIABLE_NAME}} instead.");
        }

        foreach (var (key, value) in Headers)
        {
            if (SecretTemplate.IsLiteralSecret(key, value) || LooksLikeLiteralAuthorization(key, value))
                errors.Add($"MCP server '{Name}': header '{key}' contains a literal secret. Use ${{env:VARIABLE_NAME}} instead.");
        }

        foreach (string arg in Arguments)
        {
            if (LooksLikeInlineCredential(arg))
                errors.Add($"MCP server '{Name}': an argument appears to contain a literal secret. Use ${{env:VARIABLE_NAME}} instead.");
        }

        return errors;
    }

    private static bool LooksLikeLiteralAuthorization(string key, string? value) =>
        string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(value)
        && !SecretTemplate.ContainsReference(value);

    // "--api-key=VALUE" / "--token=VALUE" style arguments.
    private static bool LooksLikeInlineCredential(string arg)
    {
        int eq = arg.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0) return false;
        string name = arg[..eq].TrimStart('-');
        string value = arg[(eq + 1)..];
        return SecretTemplate.IsLiteralSecret(name, value);
    }
}
