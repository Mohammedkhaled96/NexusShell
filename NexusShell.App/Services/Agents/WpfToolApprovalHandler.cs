using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.App.Interfaces;
using NexusShell.App.Views;

namespace NexusShell.App.Services.Agents
{
    /// <summary>
    /// UI adapter for the agent's human-in-the-loop port. Experts run in parallel,
    /// so requests are serialized: the user sees one approval dialog at a time.
    /// Without a UI dispatcher every request is denied (secure default).
    /// </summary>
    public sealed class WpfToolApprovalHandler : IToolApprovalHandler
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ISoundService _sound;
        private readonly ILogger<WpfToolApprovalHandler> _logger;

        public WpfToolApprovalHandler(ISoundService sound, ILogger<WpfToolApprovalHandler> logger)
        {
            _sound = sound;
            _logger = logger;
        }

        public async Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return ToolApprovalDecision.Deny;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await dispatcher.InvokeAsync(() =>
                {
                    try { _sound.Play(AppSound.Notify); } catch { /* audio is best-effort */ }
                    var app = Application.Current;
                    if (app is null) return ToolApprovalDecision.Deny;
                    var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
                    return ToolApprovalDialog.Show(request, owner);
                }).Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Approval dialog was cancelled (application shutting down); denying {Tool}.", request.ToolName);
                return ToolApprovalDecision.Deny;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
