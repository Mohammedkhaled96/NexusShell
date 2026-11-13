using Microsoft.Extensions.AI;

namespace NexusShell.Agents.Application.Agents;

/// <summary>
/// Bounds an expert's session memory to the most recent messages.
/// Unlike naive counting, the kept window always starts at a user turn, so a
/// tool result is never separated from the assistant tool call that produced it
/// (providers reject orphaned tool messages).
/// </summary>
internal sealed class RecentMessagesChatReducer : IChatReducer
{
    private readonly int _maxMessages;

    public RecentMessagesChatReducer(int maxMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 2);
        _maxMessages = maxMessages;
    }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var all = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (all.Count <= _maxMessages) return Task.FromResult<IEnumerable<ChatMessage>>(all);

        var system = all.Where(m => m.Role == ChatRole.System).ToList();
        var conversation = all.Where(m => m.Role != ChatRole.System).ToList();

        int start = Math.Max(0, conversation.Count - _maxMessages);
        int aligned = start;
        while (aligned < conversation.Count && conversation[aligned].Role != ChatRole.User) aligned++;

        if (aligned >= conversation.Count)
        {
            // No user turn inside the window: at least never begin with a tool result.
            aligned = start;
            while (aligned < conversation.Count && conversation[aligned].Role == ChatRole.Tool) aligned++;
        }

        return Task.FromResult<IEnumerable<ChatMessage>>(system.Concat(conversation.Skip(aligned)).ToList());
    }
}
