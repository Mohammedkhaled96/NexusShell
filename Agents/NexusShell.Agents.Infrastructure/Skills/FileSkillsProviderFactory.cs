using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Skills;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Infrastructure.Configuration;

namespace NexusShell.Agents.Infrastructure.Skills;

/// <summary>
/// Skills are folders containing a SKILL.md (Agent Skills format) under
/// <c>&lt;app&gt;\Agent\skills</c> (bundled) and <c>%AppData%\NexusShell\agent\skills</c> (user).
/// Built on the Agent Framework's <see cref="AgentSkillsProvider"/>: the model
/// first sees only skill names and descriptions and loads full instructions
/// on demand (progressive disclosure keeps prompts small).
/// </summary>
internal sealed class FileSkillsProviderFactory(
    NexusAgentPathsOptions paths,
    SkillScriptRunner scriptRunner,
    Application.ToolPackages.IToolPackageCatalog toolPackages,
    ILoggerFactory loggerFactory) : ISkillsProviderFactory
{
    /// <summary>Skill folders, plus the tool folders: a tool's SKILL.md tells the agent when and how to use it.</summary>
    public IReadOnlyList<string> SkillDirectories =>
        new[] { paths.BundledSkillsDirectory, paths.UserSkillsDirectory, paths.BundledToolsDirectory, paths.UserToolsDirectory }
            .Where(Directory.Exists)
            .ToList();

    public AIContextProvider? CreateFor(ExpertProfile expert, NexusAgentConfiguration configuration)
    {
        if (expert.Skills.Count == 0) return null;

        var directories = SkillDirectories;
        if (directories.Count == 0) return null;

        bool all = expert.Skills.Any(s => s.Trim() == "*");
        var allowed = new HashSet<string>(expert.Skills.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        var disabled = configuration.DisabledSkills;
        if (!all && allowed.All(disabled.Contains)) return null;

        bool scripts = configuration.Skills.EnableScripts;
        // A tool package's scripts run only through its own tool (its environment, progress, time limit),
        // never as loose skill scripts; of its files only the Markdown documentation is a readable resource.
        var tools = toolPackages.List().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileOptions = new AgentFileSkillsSourceOptions
        {
            SearchDepth = 2,
            ScriptFilter = context => scripts && !tools.Contains(context.SkillName),
            ResourceFilter = context => !tools.Contains(context.SkillName) || context.RelativeFilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
        };

        // The framework requires a runner for file skills; when scripts are disabled the runner refuses.
        var builder = new AgentSkillsProviderBuilder()
            .UseFileSkills(directories, fileOptions, scripts ? scriptRunner.RunAsync : SkillScriptRunner.DisabledAsync)
            .UseFilter((skill, _) => (all || allowed.Contains(skill.Frontmatter.Name)) && !disabled.Contains(skill.Frontmatter.Name))
            .UseOptions(options =>
            {
                // Reading instructions/resources is harmless; running scripts always asks the user.
                options.DisableLoadSkillApproval = true;
                options.DisableReadSkillResourceApproval = true;
                options.DisableRunSkillScriptApproval = false;
            })
            .UseLoggerFactory(loggerFactory);

        return builder.Build();
    }

    public IReadOnlyList<SkillOption> ListSkills()
    {
        // User skills override bundled skills with the same name.
        var skills = new Dictionary<string, SkillOption>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in SkillDirectories)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                try
                {
                    var document = FrontmatterDocument.Parse(File.ReadAllText(file));
                    string name = document.GetString("name") ?? Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    skills[name.Trim()] = new SkillOption(name.Trim(), document.GetString("description")?.Trim() ?? string.Empty);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    // An unreadable skill is reported by the framework when an expert loads it.
                }
            }
        }
        return skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
