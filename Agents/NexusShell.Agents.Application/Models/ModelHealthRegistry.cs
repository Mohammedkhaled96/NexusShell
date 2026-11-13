using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NexusShell.Agents.Application.Models;

/// <summary>
/// Remembers models that failed (missing, or overloaded) so the resolver skips
/// them: a circuit breaker per model. Entries expire, so a model that comes back
/// is used again without restarting the app.
/// </summary>
public interface IModelHealthRegistry
{
    bool IsUsable(string providerId, string modelId);

    void ReportUnusable(string providerId, string modelId, string reason, TimeSpan? duration = null);
}

internal sealed class ModelHealthRegistry(TimeProvider time, ILogger<ModelHealthRegistry> logger) : IModelHealthRegistry
{
    private static readonly TimeSpan DefaultQuarantine = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unusableUntil = new(StringComparer.OrdinalIgnoreCase);

    public bool IsUsable(string providerId, string modelId)
    {
        string key = Key(providerId, modelId);
        if (!_unusableUntil.TryGetValue(key, out var until)) return true;
        if (time.GetUtcNow() < until) return false;
        _unusableUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
        return true;
    }

    public void ReportUnusable(string providerId, string modelId, string reason, TimeSpan? duration = null)
    {
        var quarantine = duration ?? DefaultQuarantine;
        _unusableUntil[Key(providerId, modelId)] = time.GetUtcNow() + quarantine;
        logger.LogWarning("Model {Provider}/{Model} avoided for {Minutes} minutes: {Reason}", providerId, modelId, quarantine.TotalMinutes, reason);
    }

    private static string Key(string providerId, string modelId) => providerId + "/" + modelId;
}
