using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace NexusShell.Agents.Tests.Fakes;

/// <summary>Deterministic IChatClient: a delegate decides the reply from the messages and options it receives.</summary>
internal sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> respond) : IChatClient
{
    private readonly object _sync = new();

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (_sync) Calls.Add((list, options));
        return Task.FromResult(respond(list, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse Call(string callId, string name, IDictionary<string, object?>? arguments = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]));

    /// <summary>Instructions + system text the client received (agents may send either).</summary>
    public static string Prompt(IReadOnlyList<ChatMessage> messages, ChatOptions? options) =>
        (options?.Instructions ?? string.Empty) + "\n" +
        string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

    public static FunctionResultContent? LastToolResult(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
}
