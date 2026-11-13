using Microsoft.Extensions.AI;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Application.Abstractions.Models;

/// <summary>Port: creates an <see cref="IChatClient"/> for a provider and model.</summary>
public interface IChatClientFactory
{
    IChatClient CreateChatClient(ProviderProfile provider, string modelId);
}

/// <summary>
/// Strategy for one provider protocol (OpenAI, Anthropic, Ollama ...).
/// Add a new provider by adding one adapter class and registering it.
/// </summary>
public interface IModelProviderAdapter
{
    bool CanHandle(ProviderKind kind);

    IChatClient CreateChatClient(ProviderProfile provider, string modelId, string? apiKey, Uri? endpoint);
}

/// <summary>Port: tells whether a provider can be used right now (key present / local server reachable).</summary>
public interface IProviderAvailability
{
    Task<ProviderStatus> GetStatusAsync(ProviderProfile provider, CancellationToken cancellationToken = default);
}

public sealed record ProviderStatus(string ProviderId, bool IsAvailable, string Detail);
