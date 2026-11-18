using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;

namespace NexusShell.Agents.Application.Models;

/// <summary>
/// Circuit breaker for models and providers. A model that is missing or overloaded
/// is avoided for a while; a provider that is unreachable, rejects the key or is out
/// of quota is avoided as a whole. Entries expire, so recovery needs no restart.
/// </summary>
public interface IModelHealthRegistry
{
    bool IsUsable(string providerId, string modelId);

    void ReportUnusable(string providerId, string modelId, string reason, TimeSpan? duration = null);

    bool IsProviderUsable(string providerId);

    void ReportProviderUnusable(string providerId, string reason, TimeSpan duration);

    /// <summary>Why a provider is currently being avoided, or null when it is usable.</summary>
    string? GetProviderIssue(string providerId);

    /// <summary>Time until the first avoided model or provider becomes usable again (null when nothing is avoided).</summary>
    TimeSpan? TimeUntilNextRecovery();

    /// <summary>Quarantines the model or the whole provider, depending on the failure.</summary>
    void Report(ModelProviderException failure)
    {
        if (failure.IsProviderWide) ReportProviderUnusable(failure.ProviderId, failure.ShortReason, failure.QuarantineDuration);
        else ReportUnusable(failure.ProviderId, failure.ModelId, failure.ShortReason, failure.QuarantineDuration);
    }
}

internal sealed class ModelHealthRegistry(TimeProvider time, ILogger<ModelHealthRegistry> logger) : IModelHealthRegistry
{
    private static readonly TimeSpan DefaultQuarantine = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Reason)> _providers = new(StringComparer.OrdinalIgnoreCase);

    public bool IsUsable(string providerId, string modelId)
    {
        if (!IsProviderUsable(providerId)) return false;

        string key = providerId + "/" + modelId;
        if (!_models.TryGetValue(key, out var until)) return true;
        if (time.GetUtcNow() < until) return false;
        _models.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
        return true;
    }

    public void ReportUnusable(string providerId, string modelId, string reason, TimeSpan? duration = null)
    {
        var quarantine = duration ?? DefaultQuarantine;
        _models[providerId + "/" + modelId] = time.GetUtcNow() + quarantine;
        logger.LogWarning("Model {Provider}/{Model} avoided for {Seconds:0} seconds: {Reason}", providerId, modelId, quarantine.TotalSeconds, reason);
    }

    public bool IsProviderUsable(string providerId) => GetProviderIssue(providerId) is null;

    public void ReportProviderUnusable(string providerId, string reason, TimeSpan duration)
    {
        _providers[providerId] = (time.GetUtcNow() + duration, reason);
        logger.LogWarning("Provider {Provider} avoided for {Seconds:0} seconds: {Reason}", providerId, duration.TotalSeconds, reason);
    }

    public TimeSpan? TimeUntilNextRecovery()
    {
        var now = time.GetUtcNow();
        var pending = _models.Values.Concat(_providers.Values.Select(p => p.Until)).Where(until => until > now).ToList();
        return pending.Count == 0 ? null : pending.Min() - now;
    }

    public string? GetProviderIssue(string providerId)
    {
        if (!_providers.TryGetValue(providerId, out var entry)) return null;
        if (time.GetUtcNow() < entry.Until) return entry.Reason;
        _providers.TryRemove(new KeyValuePair<string, (DateTimeOffset, string)>(providerId, entry));
        return null;
    }
}
