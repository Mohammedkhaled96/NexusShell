using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Agents;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.Routing;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Orchestration;

namespace NexusShell.Agents.Application.Orchestration;

/// <summary>Creates the top-level agent for the configured orchestration mode (Factory pattern).</summary>
public interface IAgentOrchestratorFactory
{
    Task<AIAgent> CreateAsync(NexusAgentConfiguration configuration, CancellationToken cancellationToken = default);
}

internal sealed class AgentOrchestratorFactory(
    IExpertRouter router,
    IExpertAgentFactory expertFactory,
    ResponseSynthesizer synthesizer,
    IModelHealthRegistry modelHealth,
    ILoggerFactory loggerFactory) : IAgentOrchestratorFactory
{
    public async Task<AIAgent> CreateAsync(NexusAgentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        switch (configuration.Orchestration.Mode)
        {
            case OrchestrationMode.Coordinator:
            {
                // The lead expert sees every other expert as a sub-agent tool and decides when to delegate.
                var lead = configuration.FindExpert(configuration.Orchestration.CoordinatorExpert) is { Enabled: true } configured
                    ? configured
                    : configuration.EnabledExperts.FirstOrDefault()
                      ?? throw new AgentUnavailableException("No experts are enabled.");

                var coordinator = await expertFactory
                    .CreateAsync(lead with { SubAgents = ["*"] }, configuration, depth: 0, cancellationToken)
                    .ConfigureAwait(false);
                return coordinator.Agent;
            }

            case OrchestrationMode.Sequential:
            {
                var steps = configuration.Orchestration.Sequence
                    .Select(id => configuration.FindExpert(id))
                    .Where(e => e is { Enabled: true })
                    .Select(e => e!)
                    .ToList();
                if (steps.Count == 0)
                    throw new AgentUnavailableException("Sequential mode needs 'orchestration.sequence' to list at least one enabled expert.");

                var agents = new List<AIAgent>(steps.Count);
                foreach (var step in steps)
                    agents.Add((await expertFactory.CreateAsync(step, configuration, depth: 0, cancellationToken).ConfigureAwait(false)).Agent);

                // Agent Framework workflow: each expert receives the conversation plus the previous experts' output.
                var workflow = AgentWorkflowBuilder.BuildSequential("nexus-sequential", agents);
                return workflow.AsAIAgent(id: "nexus-sequential", name: "nexus-sequential", description: "Sequential expert pipeline");
            }

            default:
                return new MixtureOfExpertsAgent(
                    configuration,
                    router,
                    expertFactory,
                    synthesizer,
                    modelHealth,
                    loggerFactory.CreateLogger<MixtureOfExpertsAgent>());
        }
    }
}
