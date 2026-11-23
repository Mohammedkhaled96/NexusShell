using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Memory;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Context;
using NexusShell.Agents.Domain.Memory;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Application.Memory;

/// <summary>A memory to save (the id and dates are assigned by <see cref="IAgentMemory"/>).</summary>
public sealed record MemoryDraft(
    string Content,
    MemoryKind Kind = MemoryKind.Fact,
    MemorySource Source = MemorySource.User,
    string? Evidence = null,
    string? Scope = null,
    IReadOnlyList<string>? Tags = null,
    bool Pinned = false);

/// <summary>Outcome of saving: the stored entry, or why it was refused.</summary>
public sealed record MemorySaveResult(MemoryEntry? Entry, bool Updated, string? Refusal)
{
    public bool Saved => Entry is not null;

    public static MemorySaveResult Refused(string reason) => new(null, false, reason);
}

/// <summary>
/// Long-term memory of the agent (shared by every expert, every conversation and the app's
/// Memory window). Rules: short facts only, never secrets, near-duplicates are merged, the
/// least useful unpinned memories are evicted beyond the configured maximum.
/// </summary>
public interface IAgentMemory
{
    Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<MemorySaveResult> RememberAsync(MemoryDraft draft, CancellationToken cancellationToken = default);

    Task<MemorySaveResult> UpdateAsync(string id, string content, CancellationToken cancellationToken = default);

    Task<bool> SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default);

    Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes every memory; returns how many were removed.</summary>
    Task<int> ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Relevant memories for a message, best first, within a token budget (pinned ones always).</summary>
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(string query, string? folder, int maxResults, int budgetTokens, CancellationToken cancellationToken = default);

    /// <summary>Records that memories were used (improves future ranking).</summary>
    Task MarkUsedAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}

internal sealed class AgentMemory(
    IMemoryRepository repository,
    IAgentConfigurationProvider configurationProvider,
    ISecretRedactor redactor,
    TimeProvider time,
    ILogger<AgentMemory> logger) : IAgentMemory
{
    /// <summary>Two memories this similar (same folder) are one memory: the newer text replaces the older.</summary>
    internal const double DuplicateThreshold = 0.75;

    private const string SecretRefusal = "Not saved: memories must never contain passwords, API keys or tokens. Keys belong in environment variables.";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<MemoryEntry>? _entries;

    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false))
                .OrderByDescending(e => e.Pinned).ThenByDescending(e => e.UpdatedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemorySaveResult> RememberAsync(MemoryDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var settings = (await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)).Memory;
        if (Validate(draft.Content, settings) is { } refusal) return MemorySaveResult.Refused(refusal);
        if (draft.Evidence is { } evidence && IsSecret(evidence)) return MemorySaveResult.Refused(SecretRefusal);

        string content = draft.Content.Trim();
        string? scope = NormalizeScope(draft.Scope);
        var now = time.GetUtcNow();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);

            var duplicate = entries
                .Where(e => string.Equals(e.Scope, scope, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Entry: e, Similarity: MemoryRanker.Similarity(e.Content, content)))
                .Where(x => x.Similarity >= DuplicateThreshold)
                .OrderByDescending(x => x.Similarity)
                .Select(x => x.Entry)
                .FirstOrDefault();

            MemoryEntry saved;
            bool updated = duplicate is not null;
            if (duplicate is not null)
            {
                saved = duplicate with
                {
                    Content = content,
                    Kind = draft.Kind,
                    // The newer statement wins, but a user statement is never downgraded to an agent guess.
                    Source = duplicate.Source == MemorySource.User && draft.Source == MemorySource.Agent ? MemorySource.User : draft.Source,
                    Evidence = Clip(draft.Evidence) ?? duplicate.Evidence,
                    Tags = MergeTags(duplicate.Tags, draft.Tags),
                    Pinned = duplicate.Pinned || draft.Pinned,
                    UpdatedAt = now,
                };
                entries[entries.IndexOf(duplicate)] = saved;
            }
            else
            {
                saved = new MemoryEntry
                {
                    Id = NewId(entries),
                    Content = content,
                    Kind = draft.Kind,
                    Source = draft.Source,
                    Evidence = Clip(draft.Evidence),
                    Scope = scope,
                    Tags = MergeTags([], draft.Tags),
                    Pinned = draft.Pinned,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                entries.Add(saved);
                Evict(entries, settings.MaxEntries, keep: saved.Id);
            }

            await repository.SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Memory {Id} {Action} ({Kind}, {Source}, {Scope}).",
                saved.Id, updated ? "updated" : "saved", saved.Kind, saved.Source, saved.Scope is null ? "global" : "folder");
            return new MemorySaveResult(saved, updated, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemorySaveResult> UpdateAsync(string id, string content, CancellationToken cancellationToken = default)
    {
        var settings = (await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)).Memory;
        if (Validate(content, settings) is { } refusal) return MemorySaveResult.Refused(refusal);

        return await MutateAsync(id, e => e with { Content = content.Trim(), UpdatedAt = time.GetUtcNow() }, cancellationToken).ConfigureAwait(false) is { } entry
            ? new MemorySaveResult(entry, true, null)
            : MemorySaveResult.Refused($"There is no memory {id}.");
    }

    public async Task<bool> SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default) =>
        await MutateAsync(id, e => e with { Pinned = pinned, UpdatedAt = time.GetUtcNow() }, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int removed = entries.RemoveAll(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            await repository.SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Memory {Id} forgotten.", id);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int count = entries.Count;
            entries.Clear();
            await repository.SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("All {Count} memories cleared.", count);
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(string query, string? folder, int maxResults, int budgetTokens, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MemoryEntry> entries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        }
        finally
        {
            _gate.Release();
        }

        var recalled = new List<MemoryEntry>();
        int used = 0;
        foreach (var scored in MemoryRanker.Rank(entries, query, folder, maxResults, time.GetUtcNow()))
        {
            int cost = TokenEstimator.Estimate(scored.Entry.Content) + 12;
            if (used + cost > budgetTokens && recalled.Count > 0) break;
            recalled.Add(scored.Entry);
            used += cost;
        }
        return recalled;
    }

    public async Task MarkUsedAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            bool changed = false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!ids.Contains(entries[i].Id, StringComparer.OrdinalIgnoreCase)) continue;
                entries[i] = entries[i] with { LastUsedAt = now, UseCount = entries[i].UseCount + 1 };
                changed = true;
            }
            if (changed) await repository.SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MemoryEntry?> MutateAsync(string id, Func<MemoryEntry, MemoryEntry> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int index = entries.FindIndex(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            entries[index] = change(entries[index]);
            await repository.SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            return entries[index];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<MemoryEntry>> LoadAsync(CancellationToken cancellationToken) =>
        _entries ??= (await repository.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();

    private string? Validate(string? content, MemorySettings settings)
    {
        if (string.IsNullOrWhiteSpace(content)) return "Not saved: the memory is empty.";
        string text = content.Trim();
        if (text.Length > settings.MaxEntryChars)
            return $"Not saved: a memory is one short fact (at most {settings.MaxEntryChars} characters); this one has {text.Length}. Split it or keep only what matters.";
        return IsSecret(text) ? SecretRefusal : null;
    }

    /// <summary>A known secret value (from an environment variable) or anything shaped like a credential.</summary>
    private bool IsSecret(string text) => SecretPatterns.LooksLikeSecret(text) || !string.Equals(redactor.Redact(text), text, StringComparison.Ordinal);

    private static void Evict(List<MemoryEntry> entries, int maxEntries, string keep)
    {
        int excess = entries.Count - Math.Max(10, maxEntries);
        if (excess <= 0) return;
        var victims = entries
            .Where(e => !e.Pinned && e.Id != keep)
            .OrderBy(e => e.UseCount)
            .ThenBy(e => e.LastUsedAt ?? e.UpdatedAt)
            .Take(excess)
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);
        entries.RemoveAll(e => victims.Contains(e.Id));
    }

    private static string NewId(IReadOnlyCollection<MemoryEntry> existing)
    {
        while (true)
        {
            string id = "m" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            if (!existing.Any(e => e.Id == id)) return id;
        }
    }

    private static string? NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        try
        {
            return Path.GetFullPath(scope.Trim().Trim('"')).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? Clip(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= 300 ? text.Trim() : text.Trim()[..300];

    private static IReadOnlyList<string> MergeTags(IReadOnlyList<string> existing, IReadOnlyList<string>? added) =>
        existing.Concat(added ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
}
