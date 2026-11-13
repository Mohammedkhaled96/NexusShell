using NexusShell.Agents.Application.Abstractions.Models;

namespace NexusShell.Agents.Application.Models;

internal static class ModelFailures
{
    /// <summary>Finds a <see cref="ModelProviderException"/> anywhere in an exception chain (frameworks may wrap it).</summary>
    public static ModelProviderException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ModelProviderException found) return found;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find(inner) is { } nested) return nested;
                }
            }
        }
        return null;
    }
}
