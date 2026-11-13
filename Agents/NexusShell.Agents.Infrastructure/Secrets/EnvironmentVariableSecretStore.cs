using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Infrastructure.Secrets;

public sealed class SecretStoreOptions
{
    /// <summary>
    /// Where <see cref="ISecretStore.SetSecretAsync"/> persists values.
    /// User (default) = per-user environment variables (HKCU\Environment on Windows),
    /// readable only by the signed-in user; never written to any NexusShell file.
    /// Tests use Process so nothing outside the test process changes.
    /// </summary>
    public EnvironmentVariableTarget PersistTarget { get; set; } = EnvironmentVariableTarget.User;
}

/// <summary>
/// Secrets live in environment variables, never in code or configuration files.
/// Lookup order: process -> user -> machine. The user scope is read directly so
/// a key set after NexusShell started (e.g. with setx) works without a restart.
/// Values are never logged.
/// </summary>
internal sealed class EnvironmentVariableSecretStore(SecretStoreOptions options, ILogger<EnvironmentVariableSecretStore> logger) : ISecretStore
{
    public string? GetSecret(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        string? value = Read(reference.Name, EnvironmentVariableTarget.Process);
        if (value is null && OperatingSystem.IsWindows())
        {
            value = Read(reference.Name, EnvironmentVariableTarget.User)
                    ?? Read(reference.Name, EnvironmentVariableTarget.Machine);
        }
        return value;
    }

    public Task SetSecretAsync(SecretReference reference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException("Secret values must be a single line.", nameof(value));

        string trimmed = value.Trim();
        // Persisting to the user scope broadcasts WM_SETTINGCHANGE, which can take seconds: keep it off the UI thread.
        return Task.Run(() =>
        {
            var target = EffectiveTarget();
            if (target != EnvironmentVariableTarget.Process)
                Environment.SetEnvironmentVariable(reference.Name, trimmed, target);
            Environment.SetEnvironmentVariable(reference.Name, trimmed, EnvironmentVariableTarget.Process);
            logger.LogInformation("Secret {SecretName} saved to the {Target} environment.", reference.Name, target);
        }, cancellationToken);
    }

    public Task RemoveSecretAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Task.Run(() =>
        {
            var target = EffectiveTarget();
            if (target != EnvironmentVariableTarget.Process)
                Environment.SetEnvironmentVariable(reference.Name, null, target);
            Environment.SetEnvironmentVariable(reference.Name, null, EnvironmentVariableTarget.Process);
            logger.LogInformation("Secret {SecretName} removed from the {Target} environment.", reference.Name, target);
        }, cancellationToken);
    }

    private EnvironmentVariableTarget EffectiveTarget()
    {
        // User/Machine scopes are only persistent on Windows.
        if (options.PersistTarget != EnvironmentVariableTarget.Process && !OperatingSystem.IsWindows())
        {
            logger.LogWarning("Persistent environment variables are only supported on Windows; the secret is kept for this process only.");
            return EnvironmentVariableTarget.Process;
        }
        return options.PersistTarget;
    }

    private static string? Read(string name, EnvironmentVariableTarget target)
    {
        try
        {
            string? value = Environment.GetEnvironmentVariable(name, target);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}
