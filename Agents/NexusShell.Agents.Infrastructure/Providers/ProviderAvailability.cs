using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>
/// Remote providers are available when their API key environment variable is set.
/// Local providers are probed over HTTP (result cached briefly) so "auto" never
/// picks a local model whose server is not running.
/// </summary>
internal sealed class ProviderAvailability(
    ISecretStore secrets,
    IHttpClientFactory httpClientFactory,
    ILogger<ProviderAvailability> logger) : IProviderAvailability
{
    private static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ProviderStatus Status)> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProviderStatus> GetStatusAsync(ProviderProfile provider, CancellationToken cancellationToken = default)
    {
        if (!provider.Enabled) return new ProviderStatus(provider.Id, false, "Disabled in configuration.");

        if (!provider.IsLocal && provider.ApiKey is not null && !secrets.HasSecret(provider.ApiKey))
            return new ProviderStatus(provider.Id, false, $"Set the {provider.ApiKey.Name} environment variable.");

        var endpoint = ChatClientFactory.ResolveEndpoint(provider, secrets);
        if (endpoint is null && provider.Kind is ProviderKind.OpenAICompatible or ProviderKind.AzureOpenAI)
        {
            string hint = provider.EndpointVariable is not null ? $"Set the {provider.EndpointVariable.Name} environment variable." : "Configure an endpoint.";
            return new ProviderStatus(provider.Id, false, hint);
        }

        if (!provider.IsLocal)
            return new ProviderStatus(provider.Id, true, provider.ApiKey is null ? "Ready." : $"API key found in {provider.ApiKey.Name}.");

        return await ProbeAsync(provider, endpoint!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderStatus> ProbeAsync(ProviderProfile provider, Uri endpoint, CancellationToken cancellationToken)
    {
        string cacheKey = provider.Id + "|" + endpoint;
        if (_probeCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < ProbeCacheDuration)
            return cached.Status;

        Uri probeUri = provider.Kind == ProviderKind.Ollama
            ? new Uri(endpoint, "/api/tags")
            : new Uri(endpoint.ToString().TrimEnd('/') + "/models");

        ProviderStatus status;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientNames.Probe)
                .GetAsync(probeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            status = response.IsSuccessStatusCode
                ? new ProviderStatus(provider.Id, true, $"Local server running at {endpoint}.")
                : new ProviderStatus(provider.Id, false, $"Local server at {endpoint} answered {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogDebug("Local provider {Provider} not reachable at {Endpoint}.", provider.Id, endpoint);
            status = new ProviderStatus(provider.Id, false, $"Local server not running at {endpoint}.");
        }

        _probeCache[cacheKey] = (DateTimeOffset.UtcNow, status);
        return status;
    }
}
