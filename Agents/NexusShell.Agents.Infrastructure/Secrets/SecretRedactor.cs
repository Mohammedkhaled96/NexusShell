using System.Collections;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Infrastructure.Secrets;

/// <summary>
/// Replaces the VALUES of credential-like environment variables
/// (names containing KEY, TOKEN, SECRET, PASSWORD ...) with <c>[REDACTED:NAME]</c>.
/// Applied to tool results, terminal context and user messages before they are
/// sent to any model provider, and to answers before they reach the UI, so a
/// prompt-injected agent cannot exfiltrate keys by, for example, printing the
/// environment.
/// </summary>
internal sealed class SecretRedactor : ISecretRedactor
{
    private const int MinimumSecretLength = 8;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly Func<IEnumerable<KeyValuePair<string, string>>> _source;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private (DateTimeOffset LoadedAt, IReadOnlyList<KeyValuePair<string, string>> Secrets) _snapshot = (DateTimeOffset.MinValue, []);

    public SecretRedactor()
        : this(ReadEnvironment, TimeProvider.System)
    {
    }

    internal SecretRedactor(Func<IEnumerable<KeyValuePair<string, string>>> source, TimeProvider time)
    {
        _source = source;
        _time = time;
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string result = text;
        foreach (var (name, value) in Secrets())
        {
            if (result.Contains(value, StringComparison.Ordinal))
                result = result.Replace(value, $"[REDACTED:{name}]", StringComparison.Ordinal);
        }
        return result;
    }

    private IReadOnlyList<KeyValuePair<string, string>> Secrets()
    {
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            if (now - _snapshot.LoadedAt < RefreshInterval) return _snapshot.Secrets;

            var secrets = _source()
                .Where(kv => SecretTemplate.IsSensitiveName(kv.Key) && IsSecretLike(kv.Value))
                .GroupBy(kv => kv.Value, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderByDescending(kv => kv.Value.Length) // longest first: never leave a partial secret behind
                .ToList();

            _snapshot = (now, secrets);
            return secrets;
        }
    }

    private static bool IsSecretLike(string value)
    {
        if (value.Length < MinimumSecretLength) return false;
        if (bool.TryParse(value, out _) || double.TryParse(value, out _)) return false;
        // Paths (e.g. GIT_ASKPASS, XAUTHORITY) are configuration, not credentials.
        if (value.Contains(Path.DirectorySeparatorChar) && Path.IsPathRooted(value)) return false;
        return true;
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadEnvironment()
    {
        var targets = OperatingSystem.IsWindows()
            ? new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User }
            : new[] { EnvironmentVariableTarget.Process };

        foreach (var target in targets)
        {
            IDictionary variables;
            try
            {
                variables = Environment.GetEnvironmentVariables(target);
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            foreach (DictionaryEntry entry in variables)
            {
                if (entry.Key is string key && entry.Value is string value && !string.IsNullOrWhiteSpace(value))
                    yield return new KeyValuePair<string, string>(key, value.Trim());
            }
        }
    }
}
