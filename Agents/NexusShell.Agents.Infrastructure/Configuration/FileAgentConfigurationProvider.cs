using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Domain.Common;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Mcp;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Routing;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.Configuration.Dto;
using NexusShell.Agents.Infrastructure.Experts;

namespace NexusShell.Agents.Infrastructure.Configuration;

/// <summary>
/// Loads bundled defaults, overlays the user's files, validates everything and
/// maps it to the Domain. Invalid entries are skipped with a warning instead of
/// breaking the agent. Secret VALUES are rejected anywhere in configuration.
/// </summary>
internal sealed class FileAgentConfigurationProvider(
    NexusAgentPathsOptions paths,
    ILogger<FileAgentConfigurationProvider> logger) : IAgentConfigurationProvider
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] ForbiddenSecretFields = ["apiKey", "api_key", "key", "token", "secret", "password", "accessToken"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile NexusAgentConfiguration? _cached;

    public async Task<NexusAgentConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (cached is not null) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached ??= await Task.Run(Load, cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    internal NexusAgentConfiguration Load()
    {
        var warnings = new List<string>();
        if (paths.ScaffoldUserDirectory) UserDirectoryScaffold.Ensure(paths, logger);

        var bundled = ReadJson<AgentConfigDto>(Path.Combine(paths.BundledDirectory, "agent.json"), warnings) ?? new AgentConfigDto();
        var user = ReadJson<AgentConfigDto>(Path.Combine(paths.UserDirectory, "agent.json"), warnings);
        var merged = Merge(bundled, user);

        var providers = MapProviders(merged.Providers, warnings);
        var experts = MarkdownExpertLoader.Load([paths.BundledExpertsDirectory, paths.UserExpertsDirectory], warnings);
        var mcpServers = LoadMcpServers(warnings);

        var configuration = new NexusAgentConfiguration
        {
            Providers = providers,
            ModelPreference = merged.Models?.Preference ?? [],
            Orchestration = MapOrchestration(merged.Orchestration, warnings),
            ToolPolicy = MapToolPolicy(merged.Tools, warnings),
            Skills = new SkillSettings { EnableScripts = merged.Skills?.EnableScripts ?? false },
            Experts = experts,
            McpServers = mcpServers,
            SharedInstructions = ReadSharedInstructions(),
            Warnings = warnings,
        };

        foreach (string warning in warnings) logger.LogWarning("Agent configuration: {Warning}", warning);
        logger.LogInformation(
            "Agent configuration loaded: {Providers} providers, {Experts} experts, {Mcp} MCP servers, mode {Mode}.",
            providers.Count, experts.Count, mcpServers.Count, configuration.Orchestration.Mode);

        return configuration;
    }

    private string ReadSharedInstructions()
    {
        foreach (string directory in new[] { paths.UserDirectory, paths.BundledDirectory })
        {
            string file = Path.Combine(directory, "instructions.md");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }
        return string.Empty;
    }

    private static T? ReadJson<T>(string path, List<string> warnings) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"'{path}' could not be read: {ex.Message}");
            return null;
        }
    }

    internal static AgentConfigDto Merge(AgentConfigDto bundled, AgentConfigDto? user)
    {
        if (user is null) return bundled;

        var providers = new Dictionary<string, ProviderDto>(bundled.Providers ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (id, provider) in user.Providers ?? [])
            providers[id] = provider;

        var b = bundled.Orchestration ?? new OrchestrationDto();
        var u = user.Orchestration ?? new OrchestrationDto();
        var br = b.Router ?? new RouterDto();
        var ur = u.Router ?? new RouterDto();

        return new AgentConfigDto
        {
            Providers = providers,
            Models = new ModelsDto { Preference = user.Models?.Preference ?? bundled.Models?.Preference },
            Tools = new ToolsDto { AutoApprove = user.Tools?.AutoApprove ?? bundled.Tools?.AutoApprove },
            Skills = new SkillsDto { EnableScripts = user.Skills?.EnableScripts ?? bundled.Skills?.EnableScripts },
            Orchestration = new OrchestrationDto
            {
                Mode = u.Mode ?? b.Mode,
                SynthesizerModel = u.SynthesizerModel ?? b.SynthesizerModel,
                DefaultExpert = u.DefaultExpert ?? b.DefaultExpert,
                CoordinatorExpert = u.CoordinatorExpert ?? b.CoordinatorExpert,
                Sequence = u.Sequence ?? b.Sequence,
                MaxSubAgentDepth = u.MaxSubAgentDepth ?? b.MaxSubAgentDepth,
                MaxApprovalRounds = u.MaxApprovalRounds ?? b.MaxApprovalRounds,
                SharedHistoryTurns = u.SharedHistoryTurns ?? b.SharedHistoryTurns,
                ExpertMemoryMessages = u.ExpertMemoryMessages ?? b.ExpertMemoryMessages,
                Router = new RouterDto
                {
                    Strategy = ur.Strategy ?? br.Strategy,
                    Model = ur.Model ?? br.Model,
                    TopK = ur.TopK ?? br.TopK,
                    MinScore = ur.MinScore ?? br.MinScore,
                    TimeoutSeconds = ur.TimeoutSeconds ?? br.TimeoutSeconds,
                },
            },
        };
    }

    internal static Dictionary<string, ProviderProfile> MapProviders(Dictionary<string, ProviderDto>? dtos, List<string> warnings)
    {
        var result = new Dictionary<string, ProviderProfile>(Identifier.Comparer);
        foreach (var (rawId, dto) in dtos ?? [])
        {
            string id;
            try
            {
                id = Identifier.Normalize(rawId);
            }
            catch (ArgumentException)
            {
                warnings.Add($"Provider name '{rawId}' is invalid.");
                continue;
            }

            var leaked = dto.Extra?.Keys.Where(k => ForbiddenSecretFields.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList() ?? [];
            if (leaked.Count > 0)
            {
                warnings.Add($"Provider '{id}' was DISABLED: it contains a secret value ('{string.Join("', '", leaked)}'). Store the key in an environment variable and reference it with \"apiKeyEnv\".");
                continue;
            }

            if (!ProviderKindParser.TryParse(dto.Kind, out var kind))
            {
                warnings.Add($"Provider '{id}': unknown kind '{dto.Kind}'. Use openai, openai-compatible, azure-openai, anthropic or ollama.");
                continue;
            }

            Uri? endpoint = null;
            if (!string.IsNullOrWhiteSpace(dto.Endpoint) &&
                (!Uri.TryCreate(dto.Endpoint, UriKind.Absolute, out endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
            {
                warnings.Add($"Provider '{id}': endpoint '{dto.Endpoint}' is not an http(s) URL.");
                continue;
            }

            bool local = dto.Local ?? kind == ProviderKind.Ollama;
            if (endpoint is not null && !local && endpoint.Scheme != Uri.UriSchemeHttps)
            {
                warnings.Add($"Provider '{id}' was DISABLED: remote endpoints must use https so the API key is never sent in clear text (set \"local\": true for LAN servers).");
                continue;
            }

            SecretReference? apiKey;
            SecretReference? endpointVariable;
            try
            {
                apiKey = SecretReference.FromNullable(dto.ApiKeyEnv);
                endpointVariable = SecretReference.FromNullable(dto.EndpointEnv);
            }
            catch (ArgumentException ex)
            {
                warnings.Add($"Provider '{id}': {ex.Message}");
                continue;
            }

            result[id] = new ProviderProfile
            {
                Id = id,
                Kind = kind,
                Endpoint = endpoint,
                EndpointVariable = endpointVariable,
                ApiKey = apiKey,
                IsLocal = local,
                DefaultModel = string.IsNullOrWhiteSpace(dto.DefaultModel) ? null : dto.DefaultModel.Trim(),
                FallbackModels = dto.FallbackModels?.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList() ?? [],
                FastModel = string.IsNullOrWhiteSpace(dto.FastModel) ? null : dto.FastModel.Trim(),
                RequestTimeout = dto.TimeoutSeconds is int seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 1800)) : null,
                Enabled = dto.Enabled ?? true,
            };
        }
        return result;
    }

    internal static OrchestrationSettings MapOrchestration(OrchestrationDto? dto, List<string> warnings)
    {
        var defaults = new OrchestrationSettings();
        if (dto is null) return defaults;

        var mode = defaults.Mode;
        switch (dto.Mode?.Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal))
        {
            case null: break;
            case "moe" or "mixtureofexperts": mode = OrchestrationMode.MixtureOfExperts; break;
            case "coordinator" or "supervisor": mode = OrchestrationMode.Coordinator; break;
            case "sequential" or "pipeline": mode = OrchestrationMode.Sequential; break;
            default: warnings.Add($"Unknown orchestration mode '{dto.Mode}'; using moe."); break;
        }

        var router = new RouterSettings();
        if (dto.Router is { } r)
        {
            var strategy = router.Strategy;
            if (r.Strategy is not null && !Enum.TryParse(r.Strategy, ignoreCase: true, out strategy))
            {
                warnings.Add($"Unknown router strategy '{r.Strategy}'; using hybrid.");
                strategy = RoutingStrategyKind.Hybrid;
            }

            router = router with
            {
                Strategy = strategy,
                Model = string.IsNullOrWhiteSpace(r.Model) ? router.Model : r.Model,
                TopK = Math.Clamp(r.TopK ?? router.TopK, 1, 8),
                MinScore = Math.Clamp(r.MinScore ?? router.MinScore, 0, 1),
                Timeout = TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds ?? (int)router.Timeout.TotalSeconds, 2, 120)),
            };
        }

        return defaults with
        {
            Mode = mode,
            Router = router,
            SynthesizerModel = string.IsNullOrWhiteSpace(dto.SynthesizerModel) ? defaults.SynthesizerModel : dto.SynthesizerModel,
            DefaultExpert = string.IsNullOrWhiteSpace(dto.DefaultExpert) ? defaults.DefaultExpert : Identifier.Normalize(dto.DefaultExpert),
            CoordinatorExpert = string.IsNullOrWhiteSpace(dto.CoordinatorExpert) ? defaults.CoordinatorExpert : Identifier.Normalize(dto.CoordinatorExpert),
            Sequence = dto.Sequence?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Identifier.Normalize).ToList() ?? defaults.Sequence,
            MaxSubAgentDepth = Math.Clamp(dto.MaxSubAgentDepth ?? defaults.MaxSubAgentDepth, 0, 4),
            MaxApprovalRounds = Math.Clamp(dto.MaxApprovalRounds ?? defaults.MaxApprovalRounds, 1, 50),
            SharedHistoryTurns = Math.Clamp(dto.SharedHistoryTurns ?? defaults.SharedHistoryTurns, 0, 50),
            ExpertMemoryMessages = Math.Clamp(dto.ExpertMemoryMessages ?? defaults.ExpertMemoryMessages, 4, 500),
        };
    }

    internal static ToolApprovalPolicy MapToolPolicy(ToolsDto? dto, List<string> warnings)
    {
        if (dto?.AutoApprove is null) return ToolApprovalPolicy.Default;

        var set = new HashSet<ToolRisk>();
        foreach (string value in dto.AutoApprove)
        {
            if (Enum.TryParse(value.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out ToolRisk risk)) set.Add(risk);
            else warnings.Add($"Unknown tool risk '{value}' in tools.autoApprove.");
        }
        return new ToolApprovalPolicy { AutoApprove = set };
    }

    private List<McpServerDefinition> LoadMcpServers(List<string> warnings)
    {
        var servers = new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[] { paths.BundledDirectory, paths.UserDirectory })
        {
            var dto = ReadJson<McpConfigDto>(Path.Combine(directory, "mcp.json"), warnings);
            if (dto is null) continue;

            var entries = (dto.McpServers ?? []).Concat(dto.Servers ?? []);
            foreach (var (name, server) in entries)
            {
                var definition = MapMcpServer(name, server, warnings);
                if (definition is not null) servers[definition.Name] = definition;
            }
        }
        return servers.Values.ToList();
    }

    internal static McpServerDefinition? MapMcpServer(string rawName, McpServerDto dto, List<string> warnings)
    {
        string name;
        try
        {
            name = Identifier.Normalize(rawName);
        }
        catch (ArgumentException)
        {
            warnings.Add($"MCP server name '{rawName}' is invalid.");
            return null;
        }

        string type = dto.Type?.Trim().ToLowerInvariant() ?? (dto.Url is not null ? "http" : "stdio");
        var transport = type is "http" or "sse" or "streamable-http" or "streamablehttp" ? McpTransportKind.Http : McpTransportKind.Stdio;

        Uri? url = null;
        if (dto.Url is not null && !Uri.TryCreate(dto.Url, UriKind.Absolute, out url))
        {
            warnings.Add($"MCP server '{name}': url '{dto.Url}' is invalid.");
            return null;
        }

        var definition = new McpServerDefinition
        {
            Name = name,
            Transport = transport,
            Command = dto.Command,
            Arguments = dto.Args ?? [],
            WorkingDirectory = dto.Cwd,
            Url = url,
            Environment = dto.Env ?? [],
            Headers = dto.Headers ?? [],
            Enabled = dto.Disabled is true ? false : dto.Enabled ?? true,
            RequireApproval = dto.RequireApproval ?? true,
            AutoApproveTools = dto.AutoApprove ?? [],
            TrustReadOnlyHints = dto.TrustReadOnlyHints ?? false,
        };

        var errors = definition.Validate();
        if (errors.Count > 0)
        {
            warnings.AddRange(errors);
            return definition with { Enabled = false };
        }
        return definition;
    }
}
