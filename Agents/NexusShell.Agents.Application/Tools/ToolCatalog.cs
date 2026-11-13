using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Tools;

namespace NexusShell.Agents.Application.Tools;

/// <summary>Registry of every built-in tool contributed by <see cref="IAgentToolProvider"/> plugins.</summary>
public interface IToolCatalog
{
    IReadOnlyList<AgentToolDescriptor> All { get; }

    /// <summary>Resolves selectors: "*" (everything), a category ("filesystem") or a tool name ("read_file").</summary>
    IReadOnlyList<AgentToolDescriptor> Resolve(IEnumerable<string> selectors);
}

internal sealed class ToolCatalog : IToolCatalog
{
    private readonly ILogger<ToolCatalog> _logger;

    public ToolCatalog(IEnumerable<IAgentToolProvider> providers, ILogger<ToolCatalog> logger)
    {
        _logger = logger;
        var byName = new Dictionary<string, AgentToolDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            foreach (var tool in provider.GetTools())
            {
                if (!byName.TryAdd(tool.Name, tool))
                    _logger.LogWarning("Duplicate tool name '{Tool}' from category '{Category}' ignored.", tool.Name, tool.Category);
            }
        }

        All = byName.Values.OrderBy(t => t.Category, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<AgentToolDescriptor> All { get; }

    public IReadOnlyList<AgentToolDescriptor> Resolve(IEnumerable<string> selectors)
    {
        var result = new List<AgentToolDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in selectors)
        {
            string selector = raw.Trim();
            if (selector.Length == 0) continue;

            var matches = selector == "*"
                ? All
                : All.Where(t =>
                    string.Equals(t.Name, selector, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Category, selector.TrimEnd('*', '.', '/'), StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
                _logger.LogWarning("Tool selector '{Selector}' did not match any tool.", selector);

            foreach (var tool in matches)
            {
                if (seen.Add(tool.Name)) result.Add(tool);
            }
        }

        return result;
    }
}
