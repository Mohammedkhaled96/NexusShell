using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexusShell.App.Interfaces
{
    public interface IAIService
    {
        bool IsConfigured { get; }

        /// <summary>
        /// Translates a natural-language user request into a shell command.
        /// </summary>
        Task<string> GetCommandFromNaturalLanguageAsync(string userRequest, string systemContext, CancellationToken ct = default);

        /// <summary>
        /// Provides a detailed, structured explanation of the terminal output.
        /// </summary>
        Task<string> ExplainOutputAsync(string output, CancellationToken ct = default);

        /// <summary>
        /// Produces a comprehensive, long-form summary of the terminal output.
        /// </summary>
        Task<string> SummarizeOutputAsync(string output, CancellationToken ct = default);

        /// <summary>
        /// Cleans terminal output locally (no AI/HTTP call):
        /// strips ANSI codes, removes duplicate lines, collapses blank lines.
        /// When <paramref name="all"/> is true, splits into numbered sections by shell prompt.
        /// </summary>
        Task<string> CleanOutputAsync(string output, bool all, CancellationToken ct = default);

        /// <summary>
        /// Sends a chat turn using the provided full message history (role/content dicts).
        /// Returns the assistant reply string. Caller is responsible for appending it to history.
        /// </summary>
        Task<string> SendChatMessageAsync(List<Dictionary<string, string>> history, CancellationToken ct = default);
    }
}
