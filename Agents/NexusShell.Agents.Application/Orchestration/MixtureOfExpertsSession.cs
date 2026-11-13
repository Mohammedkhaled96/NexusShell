using System.Text.Json;
using Microsoft.Agents.AI;
using NexusShell.Agents.Application.Routing;

namespace NexusShell.Agents.Application.Orchestration;

/// <summary>
/// Conversation state of the Mixture-of-Experts agent:
/// a shared transcript (user turns + final answers) and one private session per
/// expert. Experts that join a conversation late receive the turns they missed
/// ("catch-up"), so every expert sees a coherent conversation.
/// </summary>
public sealed class MixtureOfExpertsSession : AgentSession
{
    private readonly object _sync = new();
    private readonly List<ConversationTurn> _transcript = [];
    private readonly Dictionary<string, ExpertSessionState> _experts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ConversationTurn> Transcript
    {
        get
        {
            lock (_sync) return _transcript.ToList();
        }
    }

    internal int TurnCount
    {
        get
        {
            lock (_sync) return _transcript.Count;
        }
    }

    internal void AppendTurn(ConversationTurn turn)
    {
        lock (_sync) _transcript.Add(turn);
    }

    internal IReadOnlyList<ConversationTurn> RecentTurns(int count)
    {
        lock (_sync) return _transcript.TakeLast(Math.Max(0, count)).ToList();
    }

    internal IReadOnlyList<ConversationTurn> TurnsSince(int index, int max)
    {
        lock (_sync) return _transcript.Skip(index).TakeLast(Math.Max(0, max)).ToList();
    }

    internal ExpertSessionState GetExpertState(string expertId)
    {
        lock (_sync)
        {
            if (!_experts.TryGetValue(expertId, out var state))
            {
                state = new ExpertSessionState();
                _experts[expertId] = state;
            }
            return state;
        }
    }

    internal IReadOnlyList<KeyValuePair<string, ExpertSessionState>> ExpertStates
    {
        get
        {
            lock (_sync) return _experts.ToList();
        }
    }
}

internal sealed class ExpertSessionState
{
    public AgentSession? Session { get; set; }

    /// <summary>Serialized session waiting for the expert agent to be created (after deserialization).</summary>
    public JsonElement? PendingSerializedSession { get; set; }

    /// <summary>Number of shared transcript turns this expert has already seen.</summary>
    public int SeenTurns { get; set; }

    public SemaphoreSlim Gate { get; } = new(1, 1);
}
