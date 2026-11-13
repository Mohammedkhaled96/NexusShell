using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.Agents.Application.Agents;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Middleware;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.Orchestration;
using NexusShell.Agents.Application.Routing;
using NexusShell.Agents.Application.Tools;

namespace NexusShell.Agents.Application;

public static class DependencyInjection
{
    /// <summary>Registers the application layer (orchestration, routing, experts, middleware).</summary>
    public static IServiceCollection AddNexusAgentsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Self-sufficient when the host has no logging configured (hosts that do win: TryAdd).
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));

        // Secure default: deny risky tools unless the host provides an interactive handler.
        services.TryAddSingleton<IToolApprovalHandler, DenyAllToolApprovalHandler>();

        services.TryAddSingleton<IToolCatalog, ToolCatalog>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IModelHealthRegistry, ModelHealthRegistry>();
        services.TryAddSingleton<IModelResolver, ModelResolver>();

        services.TryAddSingleton<KeywordRoutingGate>();
        services.TryAddSingleton<LlmRoutingGate>();
        services.TryAddSingleton<IExpertRouter, ExpertRouter>();

        services.TryAddSingleton<ResponseSynthesizer>();
        services.TryAddSingleton<ToolInvocationMiddleware>();
        services.TryAddSingleton<IExpertAgentFactory, ExpertAgentFactory>();
        services.TryAddSingleton<IAgentOrchestratorFactory, AgentOrchestratorFactory>();
        services.TryAddSingleton<INexusAgentService, NexusAgentService>();

        return services;
    }
}
