using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Memory;
using NexusShell.Agents.Domain.Memory;
using NexusShell.Agents.Infrastructure.Configuration;

namespace NexusShell.Agents.Infrastructure.Memory;

/// <summary>
/// Long-term memories in <c>%AppData%\NexusShell\agent\memory\memories.json</c>: local, private to
/// the Windows user, readable and editable. Written atomically (temporary file, then replace);
/// a damaged file is kept aside as <c>memories.corrupt-*.json</c> instead of being overwritten.
/// </summary>
internal sealed class JsonMemoryRepository(NexusAgentPathsOptions paths, ILogger<JsonMemoryRepository> logger) : IMemoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Computed properties (IsVerified) are derived from the data, never stored.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(MemoryEntry)) return;
                    foreach (var property in info.Properties.Where(p => p.Set is null).ToList())
                        info.Properties.Remove(property);
                },
            },
        },
    };

    internal string FilePath => Path.Combine(paths.UserDirectory, "memory", "memories.json");

    public async Task<IReadOnlyList<MemoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            await using var stream = File.OpenRead(FilePath);
            var file = await JsonSerializer.DeserializeAsync<MemoryFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            var entries = (file?.Memories ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Content))
                .ToList();
            logger.LogInformation("Loaded {Count} long-term memories.", entries.Count);
            return entries;
        }
        catch (JsonException ex)
        {
            string aside = Path.Combine(Path.GetDirectoryName(FilePath)!, $"memories.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(FilePath, aside, overwrite: true);
            logger.LogWarning(ex, "The memory file was damaged; it was kept as {File} and memory starts empty.", aside);
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<MemoryEntry> entries, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        string json = JsonSerializer.Serialize(new MemoryFile { Memories = entries.ToList() }, JsonOptions);
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed class MemoryFile
    {
        public int Version { get; set; } = 1;

        public List<MemoryEntry> Memories { get; set; } = [];
    }
}
