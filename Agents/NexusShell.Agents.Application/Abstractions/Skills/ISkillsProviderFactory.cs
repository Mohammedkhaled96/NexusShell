using Microsoft.Agents.AI;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Experts;

namespace NexusShell.Agents.Application.Abstractions.Skills;

/// <summary>
/// Port: builds the Agent Framework skills context provider for an expert.
/// Skills follow the Agent Skills format (a folder with SKILL.md) and use
/// progressive disclosure: only names and descriptions are advertised until
/// the model loads a skill.
/// </summary>
public interface ISkillsProviderFactory
{
    /// <summary>Returns null when the expert has no skills.</summary>
    AIContextProvider? CreateFor(ExpertProfile expert, NexusAgentConfiguration configuration);

    IReadOnlyList<string> SkillDirectories { get; }
}
