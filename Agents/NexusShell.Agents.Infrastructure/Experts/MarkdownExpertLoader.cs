using NexusShell.Agents.Domain.Common;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Models;
using NexusShell.Agents.Infrastructure.Configuration;

namespace NexusShell.Agents.Infrastructure.Experts;

/// <summary>
/// Loads experts from <c>*.md</c> files. Directories are applied in order, so a
/// user file with the same id replaces a bundled expert, and a user file with
/// only <c>enabled: false</c> switches a bundled expert off.
/// </summary>
internal static class MarkdownExpertLoader
{
    public static IReadOnlyList<ExpertProfile> Load(IEnumerable<string> directories, List<string> warnings)
    {
        var experts = new Dictionary<string, ExpertProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory)) continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(file).Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var document = FrontmatterDocument.Parse(File.ReadAllText(file));
                    string id = Identifier.Normalize(document.GetString("id") ?? Path.GetFileNameWithoutExtension(file));
                    experts.TryGetValue(id, out var existing);

                    var expert = Map(id, document, existing, file);
                    if (expert is not null) experts[id] = expert;
                }
                catch (Exception ex) when (ex is FormatException or IOException or ArgumentException or UnauthorizedAccessException)
                {
                    warnings.Add($"Expert file '{file}' was skipped: {ex.Message}");
                }
            }
        }

        return experts.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    internal static ExpertProfile? Map(string id, FrontmatterDocument document, ExpertProfile? baseline, string source)
    {
        string? name = document.GetString("name") ?? baseline?.Name;
        string? description = document.GetString("description") ?? baseline?.Description;
        string instructions = document.Body.Length > 0 ? document.Body : baseline?.Instructions ?? string.Empty;

        if (string.IsNullOrWhiteSpace(description))
            throw new FormatException("'description' is required (the router uses it to choose experts).");
        if (string.IsNullOrWhiteSpace(instructions))
            throw new FormatException("the Markdown body (the expert's instructions) is empty.");

        string model = document.GetString("model") ?? baseline?.Model ?? ModelReference.AutoKeyword;
        if (!ModelReference.TryParse(model, out _))
            throw new FormatException($"'{model}' is not a valid model reference.");

        double? temperature = document.GetDouble("temperature");
        double? maxTokens = document.GetDouble("max_output_tokens", "maxOutputTokens", "max_tokens");

        return new ExpertProfile
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Description = description,
            Instructions = instructions,
            Model = model,
            Keywords = Pick(document.GetList("keywords"), baseline?.Keywords),
            Tools = Pick(document.GetList("tools"), baseline?.Tools),
            Skills = Pick(document.GetList("skills"), baseline?.Skills),
            McpServers = Pick(document.GetList("mcp", "mcp_servers", "mcpServers"), baseline?.McpServers),
            SubAgents = Pick(document.GetList("subagents", "sub_agents", "subAgents"), baseline?.SubAgents).Select(s => s == "*" ? s : Identifier.Normalize(s)).ToList(),
            Temperature = temperature is null ? baseline?.Temperature : (float)Math.Clamp(temperature.Value, 0, 2),
            MaxOutputTokens = maxTokens is null ? baseline?.MaxOutputTokens : (int)Math.Clamp(maxTokens.Value, 64, 200_000),
            Enabled = document.GetBool("enabled") ?? baseline?.Enabled ?? true,
            Routable = document.GetBool("routable") ?? baseline?.Routable ?? true,
            Source = source,
        };
    }

    private static IReadOnlyList<string> Pick(IReadOnlyList<string> value, IReadOnlyList<string>? fallback) =>
        value.Count > 0 ? value : fallback ?? [];
}
