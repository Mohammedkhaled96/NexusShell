using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Skills;
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
    ILoggerFactory loggerFactory) : ISkillsProviderFactory
{
    public IReadOnlyList<string> SkillDirectories =>
        new[] { paths.BundledSkillsDirectory, paths.UserSkillsDirectory }
            .Where(Directory.Exists)
            .ToList();

    public AIContextProvider? CreateFor(ExpertProfile expert, NexusAgentConfiguration configuration)
    {
        if (expert.Skills.Count == 0) return null;

        var directories = SkillDirectories;
        if (directories.Count == 0) return null;

        bool all = expert.Skills.Any(s => s.Trim() == "*");
        var allowed = new HashSet<string>(expert.Skills.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);

        bool scripts = configuration.Skills.EnableScripts;
        var fileOptions = new AgentFileSkillsSourceOptions { SearchDepth = 2 };
        if (!scripts) fileOptions.ScriptFilter = _ => false; // scripts are not even advertised to the model

        // The framework requires a runner for file skills; when scripts are disabled the runner refuses.
        var builder = new AgentSkillsProviderBuilder()
            .UseFileSkills(directories, fileOptions, scripts ? scriptRunner.RunAsync : SkillScriptRunner.DisabledAsync)
            .UseFilter((skill, _) => all || allowed.Contains(skill.Frontmatter.Name))
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
}
