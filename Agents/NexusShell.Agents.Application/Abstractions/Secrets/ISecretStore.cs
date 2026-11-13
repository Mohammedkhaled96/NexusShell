using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Application.Abstractions.Secrets;

/// <summary>
/// Port for reading and writing secrets (API keys, tokens). The production
/// adapter stores them as per-user environment variables. Implementations must
/// never log secret values.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret value, or null when it is not set.</summary>
    string? GetSecret(SecretReference reference);

    bool HasSecret(SecretReference reference) => !string.IsNullOrEmpty(GetSecret(reference));

    /// <summary>Persists the secret for the current user (and makes it visible to this process immediately).</summary>
    Task SetSecretAsync(SecretReference reference, string value, CancellationToken cancellationToken = default);

    Task RemoveSecretAsync(SecretReference reference, CancellationToken cancellationToken = default);
}

/// <summary>Removes known secret values from text before it reaches a model, a log or the UI.</summary>
public interface ISecretRedactor
{
    string Redact(string text);
}
