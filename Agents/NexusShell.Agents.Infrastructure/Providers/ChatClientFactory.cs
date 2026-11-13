using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Agents;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>
/// Picks the adapter for the provider's protocol, injects the API key from the
/// secret store at call time (keys are never cached in configuration objects),
/// and adds OpenTelemetry instrumentation (message content is NOT recorded).
/// </summary>
internal sealed class ChatClientFactory(
    IEnumerable<IModelProviderAdapter> adapters,
    ISecretStore secrets,
    ILoggerFactory loggerFactory) : IChatClientFactory
{
    private readonly IReadOnlyList<IModelProviderAdapter> _adapters = adapters.ToList();

    public IChatClient CreateChatClient(ProviderProfile provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(provider.Kind))
            ?? throw new NotSupportedException($"No adapter is registered for provider kind '{provider.Kind}'.");

        string? apiKey = provider.ApiKey is null ? null : secrets.GetSecret(provider.ApiKey);
        if (!provider.IsLocal && provider.ApiKey is not null && apiKey is null)
            throw new InvalidOperationException($"The API key for '{provider.Id}' is not set. Set the {provider.ApiKey.Name} environment variable.");

        Uri? endpoint = ResolveEndpoint(provider, secrets);

        IChatClient client = new ProviderErrorChatClient(adapter.CreateChatClient(provider, modelId, apiKey, endpoint), provider, modelId);
        return client
            .AsBuilder()
            .UseOpenTelemetry(loggerFactory, AgentTelemetry.SourceName, c => c.EnableSensitiveData = false)
            .Build();
    }

    internal static Uri? ResolveEndpoint(ProviderProfile provider, ISecretStore secrets)
    {
        if (provider.EndpointVariable is not null)
        {
            string? fromEnvironment = secrets.GetSecret(provider.EndpointVariable);
            if (Uri.TryCreate(fromEnvironment, UriKind.Absolute, out var uri)) return uri;
        }

        return provider.Endpoint ?? (provider.Kind == ProviderKind.Ollama ? OllamaProviderAdapter.DefaultEndpoint : null);
    }
}
