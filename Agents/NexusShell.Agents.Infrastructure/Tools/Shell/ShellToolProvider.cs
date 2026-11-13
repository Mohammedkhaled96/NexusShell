using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Infrastructure.Tools.Shell;

/// <summary>
/// Category "shell": runs a command in a hidden PowerShell / cmd / bash process and
/// returns its output. Destructive by definition, so ALWAYS asks the user first
/// (the command text is shown in the approval prompt).
/// </summary>
internal sealed class ShellToolProvider(ILogger<ShellToolProvider> logger) : IAgentToolProvider
{
    private const int MaxOutputChars = 30_000;

    public string Category => "shell";

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new AgentToolDescriptor(
            AIFunctionFactory.Create(RunAsync, "run_shell_command",
                "Run a shell command and return exit code, stdout and stderr. The user must approve every command. Prefer read-only commands; never run interactive programs."),
            ToolRisk.Destructive,
            Category,
            ForceApproval: true);
    }

    private async Task<string> RunAsync(
        [Description("The exact command to run.")] string command,
        [Description("powershell (default on Windows), pwsh, cmd or bash.")] string? shell = null,
        [Description("Working directory (optional).")] string? workingDirectory = null,
        [Description("Timeout in seconds (5-600).")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        string selected = (shell ?? (OperatingSystem.IsWindows() ? "powershell" : "bash")).Trim().ToLowerInvariant();
        var startInfo = selected switch
        {
            "cmd" => Create("cmd.exe", "/d", "/s", "/c", command),
            "pwsh" => Create("pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command),
            "bash" or "sh" => Create(OperatingSystem.IsWindows() ? "wsl.exe" : "/bin/bash", "-c", command),
            _ => Create(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command),
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(workingDirectory));
            if (!Directory.Exists(full)) return $"Working directory not found: {full}";
            startInfo.WorkingDirectory = full;
        }

        logger.LogInformation("Agent shell command started ({Shell}, {Length} chars).", selected, command.Length);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Could not start {selected}: {ex.Message}";
        }

        process.StandardInput.Close(); // never wait for interactive input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return $"Command timed out after {timeoutSeconds} seconds and was stopped.\nPartial output:\n{Truncate(stdout.ToString())}";
        }

        var result = new StringBuilder().Append("exit code: ").Append(process.ExitCode).AppendLine();
        if (stdout.Length > 0) result.AppendLine("stdout:").AppendLine(Truncate(stdout.ToString()));
        if (stderr.Length > 0) result.AppendLine("stderr:").AppendLine(Truncate(stderr.ToString()));
        return result.ToString();
    }

    private static ProcessStartInfo Create(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb)
        {
            if (sb.Length < MaxOutputChars * 2) sb.AppendLine(line);
        }
    }

    private static string Truncate(string text) => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n... (truncated)";
}
