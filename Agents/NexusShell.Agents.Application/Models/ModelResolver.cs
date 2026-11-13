using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Models;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Application.Models;

public sealed record ResolvedModel(ProviderProfile Provider, string ModelId)
{
    public override string ToString() => $"{Provider.Id}/{ModelId}";
}

/// <summary>Turns a model reference ("provider/model", "provider", "auto") into a usable provider + model.</summary>
public interface IModelResolver
{
    /// <summary>Returns null when no configured model is currently available.</summary>
    Task<ResolvedModel?> ResolveAsync(string? requested, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Chain-of-responsibility resolution: requested model -> preference list -> any
/// available provider default. An unavailable explicit model degrades gracefully
/// to "auto" instead of failing the whole agent.
/// </summary>
internal sealed class ModelResolver(
    IAgentConfigurationProvider configurationProvider,
    IProviderAvailability availability,
    IModelHealthRegistry health,
    ILogger<ModelResolver> logger) : IModelResolver
{
    public async Task<ResolvedModel?> ResolveAsync(string? requested, CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        // "fast": same provider order as auto, but each provider's quick model first.
        bool fast = ModelReference.IsFastKeyword(requested);
        if (fast) requested = ModelReference.AutoKeyword;

        if (!ModelReference.TryParse(requested, out var reference))
        {
            logger.LogWarning("Invalid model reference '{Model}', using auto.", requested);
            reference = ModelReference.Auto;
        }

        if (!reference.IsAuto)
        {
            var explicitModel = await TryResolveAsync(config, reference, cancellationToken).ConfigureAwait(false);
            if (explicitModel is not null) return explicitModel;

            // Same provider, other models (default + fallbacks), before switching provider.
            if (reference.ModelId is not null)
            {
                var sameProvider = await TryResolveAsync(config, reference with { ModelId = null }, cancellationToken).ConfigureAwait(false);
                if (sameProvider is not null)
                {
                    logger.LogWarning("Model '{Model}' is not available; using {Fallback}.", reference, sameProvider);
                    return sameProvider;
                }
            }

            logger.LogWarning("Model '{Model}' is not available; falling back to automatic selection.", reference);
        }

        foreach (string preference in config.ModelPreference)
        {
            if (!ModelReference.TryParse(preference, out var preferred) || preferred.IsAuto) continue;
            if (fast && await TryResolveFastAsync(config, preferred.ProviderId, cancellationToken).ConfigureAwait(false) is { } quick) return quick;
            var resolved = await TryResolveAsync(config, preferred, cancellationToken).ConfigureAwait(false);
            if (resolved is not null) return resolved;
        }

        foreach (var provider in config.Providers.Values.Where(p => p.Enabled && p.CandidateModels.Any()))
        {
            if (fast && await TryResolveFastAsync(config, provider.Id, cancellationToken).ConfigureAwait(false) is { } quick) return quick;
            var resolved = await TryResolveAsync(config, new ModelReference(provider.Id, null), cancellationToken).ConfigureAwait(false);
            if (resolved is not null) return resolved;
        }

        return null;
    }

    public async Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var checks = config.Providers.Values
            .Where(p => p.Enabled)
            .Select(p => availability.GetStatusAsync(p, cancellationToken));
        return await Task.WhenAll(checks).ConfigureAwait(false);
    }

    private async Task<ResolvedModel?> TryResolveFastAsync(NexusAgentConfiguration config, string providerId, CancellationToken cancellationToken)
    {
        if (!config.Providers.TryGetValue(providerId, out var provider) || !provider.Enabled) return null;
        if (string.IsNullOrWhiteSpace(provider.FastModel) || !health.IsUsable(provider.Id, provider.FastModel)) return null;
        var status = await availability.GetStatusAsync(provider, cancellationToken).ConfigureAwait(false);
        return status.IsAvailable ? new ResolvedModel(provider, provider.FastModel) : null;
    }

    private async Task<ResolvedModel?> TryResolveAsync(NexusAgentConfiguration config, ModelReference reference, CancellationToken cancellationToken)
    {
        if (!config.Providers.TryGetValue(reference.ProviderId, out var provider) || !provider.Enabled) return null;

        // Explicit model -> only that one; provider only -> default model, then fallbacks.
        List<string> candidates = reference.ModelId is not null ? [reference.ModelId] : provider.CandidateModels.ToList();
        string? modelId = candidates.FirstOrDefault(m => health.IsUsable(provider.Id, m));
        if (modelId is null) return null;

        var status = await availability.GetStatusAsync(provider, cancellationToken).ConfigureAwait(false);
        return status.IsAvailable ? new ResolvedModel(provider, modelId) : null;
    }
}
