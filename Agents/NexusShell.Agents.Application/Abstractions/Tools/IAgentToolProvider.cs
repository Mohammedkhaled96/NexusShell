using Microsoft.Extensions.AI;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Abstractions.Tools;

/// <summary>A tool plus the metadata the agent runtime needs to govern it.</summary>
public sealed record AgentToolDescriptor(AIFunction Function, ToolRisk Risk, string Category, bool ForceApproval = false, bool SkipApproval = false)
{
    public string Name => Function.Name;
}

/// <summary>
/// Plugin contract for a tool set (one class per category, e.g. filesystem, shell, web).
/// Register implementations in DI; the tool catalog discovers them automatically.
/// </summary>
public interface IAgentToolProvider
{
    string Category { get; }

    IEnumerable<AgentToolDescriptor> GetTools();
}
