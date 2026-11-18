using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>
/// Lists the models a provider really has:
/// <list type="bullet">
/// <item>Ollama: <c>GET /api/tags</c> (installed models).</item>
/// <item>OpenAI and OpenAI-compatible servers (LM Studio, vLLM, llama.cpp, Jan, Groq, OpenRouter, Gemini ...): <c>GET {endpoint}/models</c>.</item>
/// <item>Anthropic: <c>GET /v1/models</c>.</item>
/// </list>
/// The API key is read from its environment variable only for the request and sent only to the
/// provider's own endpoint. Results are cached briefly; failures return an empty list.
/// </summary>
internal sealed class ModelDiscovery(
    ISecretStore secrets,
    IHttpClientFactory httpClientFactory,
    ILogger<ModelDiscovery> logger) : IModelDiscovery
{
    private const int MaxModels = 400;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly Uri OpenAIEndpoint = new("https://api.openai.com/v1/");
    private static readonly Uri AnthropicEndpoint = new("https://api.anthropic.com/");

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<string> Models)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderProfile provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!provider.Enabled || provider.Kind == ProviderKind.AzureOpenAI) return []; // Azure lists deployments, not models

        var endpoint = ChatClientFactory.ResolveEndpoint(provider, secrets)
            ?? provider.Kind switch
            {
                ProviderKind.OpenAI => OpenAIEndpoint,
                ProviderKind.Anthropic => AnthropicEndpoint,
                _ => null,
            };
        if (endpoint is null) return [];

        string cacheKey = provider.Id + "|" + endpoint;
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheDuration) return cached.Models;

        string? apiKey = provider.ApiKey is null ? null : secrets.GetSecret(provider.ApiKey);
        if (!provider.IsLocal && provider.ApiKey is not null && string.IsNullOrEmpty(apiKey)) return [];

        IReadOnlyList<string> models;
        try
        {
            using var request = BuildRequest(provider, endpoint, apiKey);
            using var response = await httpClientFactory.CreateClient(HttpClientNames.Discovery)
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Model list of {Provider} answered {Status}.", provider.Id, (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            models = Parse(provider.Kind, document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogDebug("Could not list models of {Provider}: {Reason}", provider.Id, ex.GetType().Name);
            return [];
        }

        _cache[cacheKey] = (DateTimeOffset.UtcNow, models);
        return models;
    }

    private static HttpRequestMessage BuildRequest(ProviderProfile provider, Uri endpoint, string? apiKey)
    {
        string root = endpoint.ToString().TrimEnd('/');
        Uri uri = provider.Kind switch
        {
            ProviderKind.Ollama => new Uri(endpoint, "/api/tags"),
            ProviderKind.Anthropic => new Uri(root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root + "/models?limit=100" : root + "/v1/models?limit=100"),
            _ => new Uri(root + "/models"),
        };

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (provider.Kind == ProviderKind.Anthropic)
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }
        return request;
    }

    /// <summary>Ollama: {"models":[{"name":..}]}; OpenAI / Anthropic: {"data":[{"id":..}]}.</summary>
    internal static IReadOnlyList<string> Parse(ProviderKind kind, JsonElement root)
    {
        var names = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) return names;

        (string array, string[] fields) = kind == ProviderKind.Ollama ? ("models", new[] { "name", "model" }) : ("data", new[] { "id", "name" });
        if (!root.TryGetProperty(array, out var items) || items.ValueKind != JsonValueKind.Array) return names;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (string field in fields)
            {
                if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    // Gemini's OpenAI-compatible list prefixes ids with "models/".
                    string name = value.GetString()!.Trim();
                    if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) name = name["models/".Length..];
                    names.Add(name);
                    break;
                }
            }
            if (names.Count >= MaxModels) break;
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
