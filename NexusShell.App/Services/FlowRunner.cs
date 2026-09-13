using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Runs flow steps as background processes (PowerShell or cmd, per the active profile),
    /// reading each step's exit code so detect-before-step and prerequisite checks are reliable.
    /// </summary>
    public sealed class FlowRunner : IFlowRunner
    {
        private readonly IFlowLibrary _library;
        private readonly ILogger<FlowRunner> _logger;
        private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(15);

        public FlowRunner(IFlowLibrary library, ILogger<FlowRunner> logger)
        {
            _library = library;
            _logger = logger;
        }

        public async Task<FlowResult> RunAsync(
            Flow flow, ShellKind shell, IProgress<FlowProgressEvent> progress,
            Func<FlowPrerequisite, Task<bool>> onPrerequisiteMissing, CancellationToken ct)
        {
            progress.Report(new FlowProgressEvent { Type = FlowEventType.FlowStarted, Message = flow.DisplayName });

            // 1. Prerequisites — detect, and prompt to install the missing ones first.
            foreach (var pre in flow.Prerequisites)
            {
                if (ct.IsCancellationRequested) return FlowResult.Stopped();

                progress.Report(new FlowProgressEvent { Type = FlowEventType.Info, Message = $"Checking prerequisite: {pre.ToolName}…" });
                if (await DetectAsync(pre.DetectCommand, shell, ct).ConfigureAwait(false))
                {
                    progress.Report(new FlowProgressEvent { Type = FlowEventType.Info, Message = $"{pre.ToolName} is present." });
                    continue;
                }

                progress.Report(new FlowProgressEvent { Type = FlowEventType.Info, Message = $"{pre.ToolName} is missing." });
                bool install = await onPrerequisiteMissing(pre).ConfigureAwait(false);
                if (!install)
                    return FlowResult.Fail($"Prerequisite '{pre.ToolName}' is required but was not installed.");

                var preFlow = pre.InstallFlowId != null ? _library.GetById(pre.InstallFlowId) : null;
                if (preFlow == null)
                    return FlowResult.Fail($"No install flow is registered for prerequisite '{pre.ToolName}'.");

                var preResult = await RunAsync(preFlow, shell, progress, onPrerequisiteMissing, ct).ConfigureAwait(false);
                if (!preResult.Success)
                    return preResult.Cancelled ? preResult : FlowResult.Fail($"Installing prerequisite '{pre.ToolName}' failed.");
            }

            // 2. Steps — detect-before-run (skip if already satisfied), then execute.
            foreach (var step in flow.Steps)
            {
                if (ct.IsCancellationRequested) return FlowResult.Stopped();

                progress.Report(new FlowProgressEvent { Type = FlowEventType.StepStarted, StepTitle = step.Title });

                if (!string.IsNullOrWhiteSpace(step.DetectCommand) &&
                    await DetectAsync(step.DetectCommand!, shell, ct).ConfigureAwait(false))
                {
                    progress.Report(new FlowProgressEvent { Type = FlowEventType.StepSkipped, StepTitle = step.Title, Message = "Already present — skipped." });
                    continue;
                }

                var cmd = step.CommandFor(shell);
                if (string.IsNullOrWhiteSpace(cmd))
                {
                    progress.Report(new FlowProgressEvent { Type = FlowEventType.StepSkipped, StepTitle = step.Title, Message = "No command for this shell — skipped." });
                    continue;
                }

                int exit;
                try
                {
                    exit = await ExecAsync(cmd!, shell, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return FlowResult.Stopped();
                }
                catch (Exception ex)
                {
                    progress.Report(new FlowProgressEvent { Type = FlowEventType.StepFailed, StepTitle = step.Title, Message = ex.Message });
                    if (step.ContinueOnError) continue;
                    return FlowResult.Fail(ex.Message);
                }

                if (exit == 0)
                {
                    progress.Report(new FlowProgressEvent { Type = FlowEventType.StepSucceeded, StepTitle = step.Title });
                }
                else
                {
                    progress.Report(new FlowProgressEvent { Type = FlowEventType.StepFailed, StepTitle = step.Title, Message = $"Exit code {exit}." });
                    if (!step.ContinueOnError)
                        return FlowResult.Fail($"Step '{step.Title}' failed (exit {exit}).");
                }
            }

            progress.Report(new FlowProgressEvent { Type = FlowEventType.FlowCompleted, Message = flow.DisplayName });
            return FlowResult.Ok();
        }

        public async Task<bool> DetectAsync(string detectCommand, ShellKind shell, CancellationToken ct = default)
        {
            try { return await ExecAsync(detectCommand, shell, progress: null, ct).ConfigureAwait(false) == 0; }
            catch { return false; }
        }

        private async Task<int> ExecAsync(string command, ShellKind shell, IProgress<FlowProgressEvent>? progress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            if (shell == ShellKind.Cmd)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + command;
            }
            else // PowerShell (WSL flows out of scope for v1)
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
            }

            // Refresh PATH from the registry so a tool installed earlier in THIS flow is found
            // without restarting the app (the app's own PATH snapshot would be stale).
            try
            {
                string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
                string user    = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                var combined = string.Join(";", new[] { machine, user }.Where(p => !string.IsNullOrEmpty(p)));
                if (combined.Length > 0) psi.Environment["PATH"] = combined;
            }
            catch { /* fall back to the inherited PATH */ }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(StepTimeout);

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => Stream(progress, e.Data);
            proc.ErrorDataReceived  += (_, e) => Stream(progress, e.Data);

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                throw;
            }

            return proc.ExitCode;
        }

        private static void Stream(IProgress<FlowProgressEvent>? progress, string? line)
        {
            if (progress != null && !string.IsNullOrEmpty(line))
                progress.Report(new FlowProgressEvent { Type = FlowEventType.StepOutput, Message = line });
        }

        private static void TryKill(Process proc)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }
}
