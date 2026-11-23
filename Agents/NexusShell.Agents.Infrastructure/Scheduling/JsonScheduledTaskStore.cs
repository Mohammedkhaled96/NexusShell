using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Chats;
using NexusShell.Agents.Domain.Scheduling;
using NexusShell.Agents.Infrastructure.Configuration;

namespace NexusShell.Agents.Infrastructure.Scheduling;

/// <summary>Scheduled tasks in <c>%AppData%\NexusShell\agent\schedule\tasks.json</c> (written atomically).</summary>
internal sealed class JsonScheduledTaskStore(NexusAgentPathsOptions paths, ILogger<JsonScheduledTaskStore> logger) : IScheduledTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal string FilePath => Path.Combine(paths.UserDirectory, "schedule", "tasks.json");

    public async Task<IReadOnlyList<ScheduledTask>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            await using var stream = File.OpenRead(FilePath);
            var file = await JsonSerializer.DeserializeAsync<TaskFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return file?.Tasks ?? [];
        }
        catch (JsonException ex)
        {
            string aside = Path.Combine(Path.GetDirectoryName(FilePath)!, $"tasks.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(FilePath, aside, overwrite: true);
            logger.LogWarning(ex, "The scheduled tasks file was damaged; it was kept as {File}.", aside);
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new TaskFile { Tasks = tasks.ToList() }, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed class TaskFile
    {
        public int Version { get; set; } = 1;

        public List<ScheduledTask> Tasks { get; set; } = [];
    }
}
