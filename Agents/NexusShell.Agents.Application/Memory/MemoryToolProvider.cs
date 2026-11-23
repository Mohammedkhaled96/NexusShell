using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Memory;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Memory;

/// <summary>
/// Category "memory": the agent saves, searches, corrects and forgets long-term memories.
/// These tools only touch the agent's own memory file (never the user's files), so they
/// run without approval and stay available in read-only mode; the user reviews and
/// edits everything in the Memory window.
/// </summary>
internal sealed class MemoryToolProvider(IAgentMemory memory, IAgentConfigurationProvider configurationProvider) : IAgentToolProvider
{
    public const string CategoryName = "memory";

    public string Category => CategoryName;

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return Describe(AIFunctionFactory.Create(RememberAsync, "remember",
            "Save one lasting fact to long-term memory so you can use it in later conversations (user preferences, project facts, decisions, corrections). Never secrets."));
        yield return Describe(AIFunctionFactory.Create(RecallAsync, "recall_memory",
            "Search long-term memory for facts saved in earlier conversations."));
        yield return Describe(AIFunctionFactory.Create(UpdateAsync, "update_memory",
            "Correct a saved memory (by its id) when it is wrong or out of date."));
        yield return Describe(AIFunctionFactory.Create(ForgetAsync, "forget_memory",
            "Delete a saved memory by its id (when the user asks, or when it is wrong)."));
    }

    private AgentToolDescriptor Describe(AIFunction function) => new(function, ToolRisk.ReadOnly, CategoryName, SkipApproval: true);

    private async Task<string> RememberAsync(
        [Description("The fact, as one short self-contained sentence (who/what/where), e.g. \"The user prefers PowerShell 7 commands\".")] string content,
        [Description("fact, preference, project, correction or decision.")] string kind = "fact",
        [Description("user = the user said it; tool = a tool showed it; agent = your own conclusion (unverified).")] string source = "user",
        [Description("Optional: where it comes from (file, command, the user's words).")] string? evidence = null,
        [Description("True when the fact only applies to the current working folder (a project).")] bool thisFolderOnly = false,
        CancellationToken cancellationToken = default)
    {
        string? folder = AgentRunScope.Current?.WorkingDirectory;
        var result = await memory.RememberAsync(new MemoryDraft(
            content,
            ParseKind(kind),
            ParseSource(source),
            evidence,
            thisFolderOnly ? folder : null), cancellationToken).ConfigureAwait(false);

        if (!result.Saved) return result.Refusal!;
        AgentRunScope.Report(new AgentProgress(AgentProgressKind.MemorySaved, $"Saved memory {result.Entry!.Id}") { Count = 1 });
        return $"{(result.Updated ? "Updated" : "Saved")} memory [{result.Entry.Id}]{(result.Entry.Scope is null ? string.Empty : " for " + result.Entry.Scope)}.";
    }

    private async Task<string> RecallAsync(
        [Description("Words to search for.")] string query,
        [Description("Maximum results (1-30).")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var settings = (await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)).Memory;
        var found = await memory.RecallAsync(query, AgentRunScope.Current?.WorkingDirectory, Math.Clamp(maxResults, 1, 30), Math.Max(500, settings.RecallBudgetTokens * 2), cancellationToken).ConfigureAwait(false);
        if (found.Count == 0) return "No saved memory matches. Do not guess: ask the user or check with a tool.";

        await memory.MarkUsedAsync(found.Select(e => e.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var entry in found)
        {
            sb.Append('[').Append(entry.Id).Append("] (").Append(MemoryPrompt.SourceText(entry.Source)).Append(", ")
              .Append(entry.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(") ").AppendLine(entry.Content);
        }
        return sb.ToString();
    }

    private async Task<string> UpdateAsync(
        [Description("Memory id, e.g. m3f9a2c.")] string id,
        [Description("The corrected fact.")] string content,
        CancellationToken cancellationToken = default)
    {
        var result = await memory.UpdateAsync(id, content, cancellationToken).ConfigureAwait(false);
        return result.Saved ? $"Memory [{result.Entry!.Id}] corrected." : result.Refusal!;
    }

    private async Task<string> ForgetAsync(
        [Description("Memory id, e.g. m3f9a2c.")] string id,
        CancellationToken cancellationToken = default) =>
        await memory.ForgetAsync(id, cancellationToken).ConfigureAwait(false) ? $"Memory [{id}] deleted." : $"There is no memory {id}.";

    internal static MemoryKind ParseKind(string? kind) =>
        Enum.TryParse<MemoryKind>(kind?.Trim(), ignoreCase: true, out var parsed) ? parsed : MemoryKind.Fact;

    internal static MemorySource ParseSource(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "tool" or "verified" => MemorySource.Tool,
        "agent" or "assistant" or "inferred" => MemorySource.Agent,
        _ => MemorySource.User,
    };
}
