using System;
using System.Threading;
using System.Threading.Tasks;
using NexusShell.App.Models;

namespace NexusShell.App.Interfaces
{
    public enum FlowEventType
    {
        FlowStarted, StepStarted, StepOutput, StepSkipped, StepSucceeded,
        StepFailed, FlowCompleted, FlowFailed, FlowCancelled, Info
    }

    /// <summary>A single progress notification raised while a flow runs.</summary>
    public sealed class FlowProgressEvent
    {
        public FlowEventType Type { get; init; }
        public string? StepTitle { get; init; }
        public string? Message { get; init; }
    }

    public sealed class FlowResult
    {
        public bool Success { get; init; }
        public bool Cancelled { get; init; }
        public string? Error { get; init; }

        public static FlowResult Ok()                 => new() { Success = true };
        public static FlowResult Fail(string error)   => new() { Success = false, Error = error };
        public static FlowResult Stopped()             => new() { Success = false, Cancelled = true };
    }

    /// <summary>
    /// Runs flow steps as background processes (exit-code aware), so detect-before-step and
    /// prerequisite checks are reliable — unlike the interactive ConPTY terminal.
    /// </summary>
    public interface IFlowRunner
    {
        /// <summary>
        /// Runs <paramref name="flow"/> for the active <paramref name="shell"/>, reporting via
        /// <paramref name="progress"/>. When a prerequisite is missing, <paramref name="onPrerequisiteMissing"/>
        /// is awaited to ask the user; returning true runs the prerequisite's install flow first.
        /// </summary>
        Task<FlowResult> RunAsync(
            Flow flow,
            ShellKind shell,
            IProgress<FlowProgressEvent> progress,
            Func<FlowPrerequisite, Task<bool>> onPrerequisiteMissing,
            CancellationToken ct);

        /// <summary>Runs a detection command; returns true when it exits 0.</summary>
        Task<bool> DetectAsync(string detectCommand, ShellKind shell, CancellationToken ct = default);
    }
}
