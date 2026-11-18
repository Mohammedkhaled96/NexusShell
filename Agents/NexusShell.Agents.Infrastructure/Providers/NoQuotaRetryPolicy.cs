using System.ClientModel.Primitives;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>
/// SDK retry policy: one quick retry for transient failures (network blips, 5xx),
/// but never for HTTP 429. A quota error is handed straight to NexusAgent, which
/// switches to another provider immediately instead of sleeping through the
/// provider's Retry-After (often 30-60 s) inside the SDK.
/// </summary>
internal sealed class NoQuotaRetryPolicy() : ClientRetryPolicy(maxRetries: 1)
{
    private const int TooManyRequests = 429;

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        message.Response?.Status != TooManyRequests && base.ShouldRetry(message, exception);

    protected override ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        message.Response?.Status == TooManyRequests ? ValueTask.FromResult(false) : base.ShouldRetryAsync(message, exception);
}
