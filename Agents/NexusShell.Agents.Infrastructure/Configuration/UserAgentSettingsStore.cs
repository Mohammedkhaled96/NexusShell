using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Domain.Common;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Infrastructure.Configuration;

/// <summary>
/// Writes provider and model choices into the USER agent.json (<c>%AppData%\NexusShell\agent</c>),
/// never into the bundled defaults. Only the fields the user set are written; the configuration
/// merge keeps every other bundled field. Secrets are refused: a provider stores the NAME of the
/// environment variable that holds its key, and values that look like keys are rejected.
/// </summary>
internal sealed class UserAgentSettingsStore(
    NexusAgentPathsOptions paths,
    IAgentConfigurationProvider configuration,
    ILogger<UserAgentSettingsStore> logger) : IAgentSettingsStore
{
    private const int MaxModelIdLength = 200;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string FilePath => Path.Combine(paths.UserDirectory, "agent.json");

    public async Task SaveProviderAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var (id, kind, endpoint) = Validate(provider);
        var effective = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        await EditAsync(root =>
        {
            var providers = Object(root, "providers");
            var entry = providers[id] as JsonObject ?? new JsonObject();
            entry["kind"] = KindName(kind);
            Set(entry, "endpoint", endpoint?.ToString());
            Set(entry, "apiKeyEnv", string.IsNullOrWhiteSpace(provider.ApiKeyEnv) ? null : provider.ApiKeyEnv.Trim());
            entry["local"] = provider.Local;
            Set(entry, "defaultModel", string.IsNullOrWhiteSpace(provider.DefaultModel) ? null : provider.DefaultModel.Trim());
            var models = CleanModels(provider.Models);
            if (models.Count > 0) entry["models"] = new JsonArray(models.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
            else entry.Remove("models");
            entry["enabled"] = true;
            providers[id] = entry;

            if (provider.Prefer)
            {
                // Put the provider first in the automatic order; keep the rest as it is today.
                var current = Strings(root["models"]?["preference"]) ?? effective.ModelPreference.ToList();
                var order = new List<string> { id };
                order.AddRange(current.Where(p => !string.Equals(p.Split('/')[0].Trim(), id, StringComparison.OrdinalIgnoreCase)));
                Object(root, "models")["preference"] = new JsonArray(order.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            }
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Provider {Provider} saved to the user agent.json.", id);
    }

    public async Task RemoveProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(providerId);
        var effective = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        bool bundled = IsBundled(id);

        await EditAsync(root =>
        {
            var providers = Object(root, "providers");
            // A bundled provider cannot be deleted: switch it off instead.
            if (bundled) providers[id] = new JsonObject { ["enabled"] = false };
            else providers.Remove(id);

            var current = Strings(root["models"]?["preference"]);
            if (current is not null)
            {
                Object(root, "models")["preference"] = new JsonArray(current
                    .Where(p => !string.Equals(p.Split('/')[0].Trim(), id, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            }
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Provider {Provider} {Action} in the user agent.json ({Count} providers configured before).",
            id, bundled ? "turned off" : "removed", effective.Providers.Count);
    }

    public async Task AddModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(providerId);
        var model = CleanModels([modelId]);
        if (model.Count == 0) throw new ArgumentException("Type a model name.", nameof(modelId));

        var effective = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!effective.Providers.ContainsKey(id)) throw new ArgumentException($"There is no provider named '{id}'. Add the provider first.", nameof(providerId));

        await EditAsync(root =>
        {
            var providers = Object(root, "providers");
            var entry = providers[id] as JsonObject ?? new JsonObject();
            var models = Strings(entry["models"]) ?? [];
            if (!models.Contains(model[0], StringComparer.OrdinalIgnoreCase)) models.Add(model[0]);
            entry["models"] = new JsonArray(models.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
            providers[id] = entry;
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Model {Model} added to provider {Provider}.", model[0], id);
    }

    internal static (string Id, ProviderKind Kind, Uri? Endpoint) Validate(ProviderSettings provider)
    {
        string id = NormalizeId(provider.Id);
        if (!ProviderKindParser.TryParse(provider.Kind, out var kind))
            throw new ArgumentException($"Unknown provider type '{provider.Kind}'. Use openai, openai-compatible, azure-openai, anthropic or ollama.");

        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            if (!Uri.TryCreate(provider.Endpoint.Trim(), UriKind.Absolute, out endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("The address must start with https:// (or http:// for a server on this computer or network).");
            if (!provider.Local && endpoint.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Online providers must use https so the API key is never sent in clear text. Tick 'Runs locally' for a server on your computer or network.");
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.Query.Contains("key=", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Do not put a key in the address. Store the key in an environment variable instead.");
        }
        else if (kind is ProviderKind.OpenAICompatible or ProviderKind.AzureOpenAI)
        {
            throw new ArgumentException("This provider type needs an address (endpoint).");
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnv))
        {
            string name = provider.ApiKeyEnv.Trim();
            if (LooksLikeSecretValue(name))
                throw new ArgumentException("That looks like an API key, not a variable name. Type a NAME such as MYPROVIDER_API_KEY and save the key itself with 'Set key'.");
            if (!SecretReference.IsValidName(name))
                throw new ArgumentException($"'{name}' is not a valid environment variable name. Use letters, digits and _ (for example MYPROVIDER_API_KEY).");
        }
        else if (!provider.Local && kind != ProviderKind.Ollama)
        {
            throw new ArgumentException("Online providers need the name of the environment variable that holds the API key.");
        }

        return (id, kind, endpoint);
    }

    /// <summary>Real keys are long and mix upper case, lower case and digits; variable names rarely do.</summary>
    internal static bool LooksLikeSecretValue(string value) =>
        value.Length >= 24
        && value.Any(char.IsAsciiLetterUpper)
        && value.Any(char.IsAsciiLetterLower)
        && value.Any(char.IsAsciiDigit)
        || value.StartsWith("sk-", StringComparison.Ordinal)
        || value.StartsWith("AIza", StringComparison.Ordinal)
        || value.StartsWith("gsk_", StringComparison.Ordinal)
        || value.StartsWith("hf_", StringComparison.Ordinal)
        || value.StartsWith("xai-", StringComparison.Ordinal)
        || value.StartsWith("ghp_", StringComparison.Ordinal)
        || value.StartsWith("github_pat_", StringComparison.Ordinal);

    private static string NormalizeId(string? value)
    {
        try
        {
            return Identifier.Normalize(value ?? string.Empty);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"'{value}' is not a valid provider name. Use letters, digits and dashes (for example my-server).");
        }
    }

    private static List<string> CleanModels(IEnumerable<string>? models) => (models ?? [])
        .Where(m => !string.IsNullOrWhiteSpace(m))
        .Select(m => m.Trim())
        .Where(m => m.Length <= MaxModelIdLength && !m.Any(char.IsControl) && !m.Contains(' ', StringComparison.Ordinal))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string KindName(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAI => "openai",
        ProviderKind.OpenAICompatible => "openai-compatible",
        ProviderKind.AzureOpenAI => "azure-openai",
        ProviderKind.Anthropic => "anthropic",
        ProviderKind.Ollama => "ollama",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private bool IsBundled(string id)
    {
        string bundled = Path.Combine(paths.BundledDirectory, "agent.json");
        if (!File.Exists(bundled)) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(bundled), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root?["providers"] is JsonObject providers && providers.Any(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task EditAsync(Action<JsonObject> edit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.UserDirectory);
            JsonObject root = new();
            if (File.Exists(FilePath))
            {
                string text = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                        ?? throw new InvalidOperationException("agent.json must contain a JSON object.");
                    // Comments are not kept when the file is rewritten: keep the original once.
                    string backup = FilePath + ".bak";
                    if (text.Contains("//", StringComparison.Ordinal) && !File.Exists(backup)) File.Copy(FilePath, backup);
                }
            }

            edit(root);

            // Write to a temporary file first so a crash never leaves a half-written configuration.
            string temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        configuration.Invalidate();
    }

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static void Set(JsonObject entry, string name, string? value)
    {
        if (value is null) entry.Remove(name);
        else entry[name] = value;
    }

    private static List<string>? Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(n => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
            : null;
}
