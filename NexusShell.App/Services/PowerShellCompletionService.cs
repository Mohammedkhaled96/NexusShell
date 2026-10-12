using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NexusShell.App.Services
{
    public record CompletionItem(
        string CompletionText,
        string ListItemText,
        string ResultType,
        string ToolTip,
        int ReplacementIndex,
        int ReplacementLength
    );

    public interface IPowerShellCompletionService : IDisposable
    {
        Task InitializeAsync();
        Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(string input, int cursorPosition, CancellationToken ct);
    }

    public sealed class PowerShellCompletionService : IPowerShellCompletionService
    {
        private readonly ILogger<PowerShellCompletionService> _logger;
        private Runspace? _runspace;
        private PowerShell? _powerShell;
        private bool _isInitialized;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public PowerShellCompletionService(ILogger<PowerShellCompletionService> logger)
        {
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await Task.Run(() =>
            {
                try
                {
                    var initialSessionState = InitialSessionState.CreateDefault2();
                    _runspace = RunspaceFactory.CreateRunspace(initialSessionState);
                    _runspace.Open();
                    _powerShell = PowerShell.Create();
                    _powerShell.Runspace = _runspace;
                    _isInitialized = true;
                    _logger.LogInformation("Dedicated PowerShell Completion Runspace initialized successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize PowerShell Completion Runspace.");
                }
            });
        }

        public async Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(string input, int cursorPosition, CancellationToken ct)
        {
            if (!_isInitialized || string.IsNullOrEmpty(input) || _powerShell == null)
                return Array.Empty<CompletionItem>();

            if (cursorPosition < 0 || cursorPosition > input.Length)
                cursorPosition = input.Length;

            await _lock.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return Array.Empty<CompletionItem>();

                    var completion = CommandCompletion.CompleteInput(
                        input,
                        cursorPosition,
                        null,
                        _powerShell
                    );

                    if (completion == null || completion.CompletionMatches == null || completion.CompletionMatches.Count == 0)
                        return (IReadOnlyList<CompletionItem>)Array.Empty<CompletionItem>();

                    var results = new List<CompletionItem>(completion.CompletionMatches.Count);
                    foreach (var match in completion.CompletionMatches)
                    {
                        results.Add(new CompletionItem(
                            CompletionText: match.CompletionText,
                            ListItemText: match.ListItemText,
                            ResultType: match.ResultType.ToString(),
                            ToolTip: match.ToolTip,
                            ReplacementIndex: completion.ReplacementIndex,
                            ReplacementLength: completion.ReplacementLength
                        ));
                    }

                    return results;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<CompletionItem>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error occurred while executing PowerShell CommandCompletion.");
                return Array.Empty<CompletionItem>();
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            _powerShell?.Dispose();
            _runspace?.Dispose();
            _lock.Dispose();
        }
    }
}
