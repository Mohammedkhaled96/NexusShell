using NexusShell.Agents.Application.Abstractions.Tools;

namespace NexusShell.Agents.Application.Abstractions.Mcp;

/// <summary>Port: connects to configured MCP servers and exposes their tools.</summary>
public interface IMcpToolProvider
{
    /// <summary>Tools of the selected servers (names or "*"). Unreachable servers are skipped and reported in status.</summary>
    Task<IReadOnlyList<AgentToolDescriptor>> GetToolsAsync(IEnumerable<string> serverSelectors, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpServerStatus>> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record McpServerStatus(string Name, bool IsConnected, int ToolCount, string Detail);
