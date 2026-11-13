using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace NexusShell.Agents.Infrastructure.Skills;

/// <summary>
/// Runs a skill's bundled script (PowerShell, Python or Node) as a child process.
/// Only used when <c>skills.enableScripts</c> is true, and every run still
/// requires user approval. Arguments are passed as one JSON string argument;
/// output is captured, truncated and bounded by a timeout.
/// </summary>
internal sealed class SkillScriptRunner(ILogger<SkillScriptRunner> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private const int MaxOutputChars = 30_000;

    /// <summary>Runner used while <c>skills.enableScripts</c> is false: never executes anything.</summary>
    public static Task<object?> DisabledAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>("Skill scripts are disabled. Enable \"skills.enableScripts\" in agent.json to allow them (each run still asks the user).");

    public async Task<object?> RunAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        string path = script.FullPath;
        var (fileName, prefixArgs) = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ps1" => (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }),
            ".py" => (OperatingSystem.IsWindows() ? "python.exe" : "python3", new[] { path }),
            ".js" or ".mjs" => ("node", new[] { path }),
            _ => throw new NotSupportedException($"Unsupported skill script type: {Path.GetFileName(path)}"),
        };

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in prefixArgs) startInfo.ArgumentList.Add(arg);
        if (arguments is { } json) startInfo.ArgumentList.Add(json.GetRawText());

        logger.LogInformation("Running skill script {Script} for skill {Skill}.", Path.GetFileName(path), skill.Frontmatter.Name);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return $"Script timed out after {Timeout.TotalSeconds:0} seconds.";
        }

        string output = await stdout.ConfigureAwait(false);
        string errors = await stderr.ConfigureAwait(false);
        var result = new StringBuilder().Append("exit code: ").Append(process.ExitCode).AppendLine();
        if (output.Length > 0) result.AppendLine(Truncate(output));
        if (errors.Length > 0) result.AppendLine("stderr:").AppendLine(Truncate(errors));
        return result.ToString();
    }

    private static string Truncate(string text) => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n... (truncated)";
}
