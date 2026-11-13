using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Tools;
using NexusShell.App.ViewModels;

namespace NexusShell.App.Services.Agents
{
    /// <summary>
    /// App-provided agent tool set (category "terminal"): lets experts read what the
    /// user sees in the active NexusShell tab. Demonstrates the agent plugin
    /// extension point: the presentation layer contributes tools without the
    /// agent layers knowing anything about WPF.
    /// </summary>
    public sealed partial class TerminalToolProvider : IAgentToolProvider
    {
        private const int MaxChars = 30_000;
        private readonly IServiceProvider _services;

        public TerminalToolProvider(IServiceProvider services) => _services = services;

        public string Category => "terminal";

        public IEnumerable<AgentToolDescriptor> GetTools()
        {
            yield return new AgentToolDescriptor(
                AIFunctionFactory.Create(GetTerminalOutput, "get_terminal_output",
                    "Read the most recent output of the user's active NexusShell terminal tab (commands and their results)."),
                ToolRisk.ReadOnly,
                Category);
        }

        private string GetTerminalOutput(
            [Description("Maximum number of characters to return, newest output last.")] int maxChars = 6000)
        {
            // MainViewModel is resolved lazily (at call time) to avoid a construction-time dependency cycle.
            var main = _services.GetRequiredService<MainViewModel>();
            var tab = Application.Current?.Dispatcher.Invoke(() => main.SelectedTab);
            if (tab is null) return "No terminal tab is open.";

            string raw = tab.GetRecentOutput(Math.Clamp(maxChars, 200, MaxChars) * 2);
            string text = AnsiRegex().Replace(raw, string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            int limit = Math.Clamp(maxChars, 200, MaxChars);
            if (text.Length > limit) text = text[^limit..];
            return string.IsNullOrWhiteSpace(text) ? "The active terminal has no output yet." : text;
        }

        // CSI, OSC and two-character escape sequences.
        [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]")]
        private static partial Regex AnsiRegex();
    }
}
