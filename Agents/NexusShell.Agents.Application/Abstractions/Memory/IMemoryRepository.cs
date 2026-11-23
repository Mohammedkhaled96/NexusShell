using NexusShell.Agents.Domain.Memory;

namespace NexusShell.Agents.Application.Abstractions.Memory;

/// <summary>
/// Port: persistence of long-term memories (a local file per Windows user). Pure storage:
/// validation, secret refusal, de-duplication and eviction live in <c>AgentMemory</c>.
/// </summary>
public interface IMemoryRepository
{
    Task<IReadOnlyList<MemoryEntry>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored memories (written atomically).</summary>
    Task SaveAsync(IReadOnlyList<MemoryEntry> entries, CancellationToken cancellationToken = default);
}
