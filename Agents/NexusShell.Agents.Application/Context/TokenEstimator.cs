using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NexusShell.Agents.Application.Context;

/// <summary>
/// Fast, provider-independent token estimate (no tokenizer download): about 4 characters
/// per token for Latin text and code, about 2 for other scripts (Arabic, CJK), plus a small
/// per-message overhead. Deliberately slightly pessimistic so budgets never overflow.
/// </summary>
public static class TokenEstimator
{
    private const int MessageOverhead = 4;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int ascii = 0;
        foreach (char ch in text)
        {
            if (ch < 128) ascii++;
        }
        int other = text.Length - ascii;
        return (ascii + 3) / 4 + (other + 1) / 2;
    }

    public static int Estimate(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int tokens = MessageOverhead;
        foreach (var content in message.Contents)
        {
            tokens += content switch
            {
                TextContent text => Estimate(text.Text),
                TextReasoningContent reasoning => Estimate(reasoning.Text),
                FunctionCallContent call => Estimate(call.Name) + Estimate(Serialize(call.Arguments)),
                FunctionResultContent result => Estimate(result.Result as string ?? Serialize(result.Result)),
                DataContent data => 1_000, // images / files: rough flat cost
                _ => 16,
            };
        }
        return tokens;
    }

    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    private static string Serialize(object? value)
    {
        if (value is null) return string.Empty;
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
