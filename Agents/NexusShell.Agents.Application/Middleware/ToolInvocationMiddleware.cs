using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Progress;

namespace NexusShell.Agents.Application.Middleware;

/// <summary>
/// Agent Framework function-invocation middleware applied to every expert:
/// <list type="bullet">
/// <item>reports tool activity (progress / screen-reader status);</item>
/// <item>audits tool calls (name, duration, outcome — never argument values);</item>
/// <item>redacts known secret values from tool results BEFORE they are sent to a model provider;</item>
/// <item>turns tool exceptions into a message for the model instead of aborting the whole run.</item>
/// </list>
/// </summary>
internal sealed class ToolInvocationMiddleware(ISecretRedactor redactor, ILogger<ToolInvocationMiddleware> logger)
{
    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        string tool = context.Function.Name;
        string agentName = agent.Name ?? agent.Id;

        AgentRunScope.Report(AgentProgressKind.ToolStarted, $"{agentName}: running tool {tool}", agentName);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            object? result = await next(context, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Tool {Tool} completed for {Agent} in {ElapsedMs} ms.", tool, agentName, stopwatch.ElapsedMilliseconds);
            AgentRunScope.Report(AgentProgressKind.ToolCompleted, $"{agentName}: tool {tool} finished", agentName);
            return RedactResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool {Tool} failed for {Agent} after {ElapsedMs} ms.", tool, agentName, stopwatch.ElapsedMilliseconds);
            AgentRunScope.Report(AgentProgressKind.Warning, $"{agentName}: tool {tool} failed", agentName);
            return $"Tool '{tool}' failed: {redactor.Redact(ex.Message)}";
        }
    }

    private object? RedactResult(object? result)
    {
        switch (result)
        {
            case null:
                return null;
            case string text:
                return redactor.Redact(text);
            case JsonElement { ValueKind: JsonValueKind.String } stringElement:
            {
                string value = stringElement.GetString() ?? string.Empty;
                string redacted = redactor.Redact(value);
                return value == redacted ? stringElement : redacted;
            }
            case JsonElement element:
            {
                string raw = element.GetRawText();
                string redacted = redactor.Redact(raw);
                return ReferenceEquals(raw, redacted) || raw == redacted ? element : redacted;
            }
            default:
            {
                string raw;
                try
                {
                    raw = JsonSerializer.Serialize(result, result.GetType(), AIJsonUtilities.DefaultOptions);
                }
                catch (Exception)
                {
                    raw = result.ToString() ?? string.Empty;
                }
                string redacted = redactor.Redact(raw);
                return raw == redacted ? result : redacted;
            }
        }
    }
}
