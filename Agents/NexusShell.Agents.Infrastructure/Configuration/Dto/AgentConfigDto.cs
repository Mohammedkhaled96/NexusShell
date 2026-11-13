using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusShell.Agents.Infrastructure.Configuration.Dto;

// JSON shapes of agent.json and mcp.json. Kept separate from the Domain so the
// file format can evolve without touching business types (mapped + validated
// by FileAgentConfigurationProvider). Every field is optional so a user file
// can override just one setting.

internal sealed class AgentConfigDto
{
    public OrchestrationDto? Orchestration { get; set; }

    public ModelsDto? Models { get; set; }

    public Dictionary<string, ProviderDto>? Providers { get; set; }

    public ToolsDto? Tools { get; set; }

    public SkillsDto? Skills { get; set; }
}

internal sealed class OrchestrationDto
{
    public string? Mode { get; set; }

    public RouterDto? Router { get; set; }

    public string? SynthesizerModel { get; set; }

    public string? DefaultExpert { get; set; }

    public string? CoordinatorExpert { get; set; }

    public List<string>? Sequence { get; set; }

    public int? MaxSubAgentDepth { get; set; }

    public int? MaxApprovalRounds { get; set; }

    public int? SharedHistoryTurns { get; set; }

    public int? ExpertMemoryMessages { get; set; }
}

internal sealed class RouterDto
{
    public string? Strategy { get; set; }

    public string? Model { get; set; }

    public int? TopK { get; set; }

    public double? MinScore { get; set; }

    public int? TimeoutSeconds { get; set; }
}

internal sealed class ModelsDto
{
    public List<string>? Preference { get; set; }
}

internal sealed class ProviderDto
{
    public string? Kind { get; set; }

    public string? Endpoint { get; set; }

    /// <summary>Name of the environment variable holding the endpoint.</summary>
    public string? EndpointEnv { get; set; }

    /// <summary>Name of the environment variable holding the API key (never the key itself).</summary>
    public string? ApiKeyEnv { get; set; }

    public bool? Local { get; set; }

    public string? DefaultModel { get; set; }

    /// <summary>Tried in order when the default model is unavailable (retired or not enabled for the key).</summary>
    public List<string>? FallbackModels { get; set; }

    /// <summary>Small, quick model for routing ("fast").</summary>
    public string? FastModel { get; set; }

    /// <summary>Maximum seconds for one model call before failing over (default 60 online, 180 local).</summary>
    public int? TimeoutSeconds { get; set; }

    public bool? Enabled { get; set; }

    /// <summary>Unknown properties: used to detect forbidden literal secrets such as an "apiKey" field with a value.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class ToolsDto
{
    /// <summary>Risk levels that run without asking: readOnly, network, mutating, destructive.</summary>
    public List<string>? AutoApprove { get; set; }
}

internal sealed class SkillsDto
{
    public bool? EnableScripts { get; set; }
}

internal sealed class McpConfigDto
{
    /// <summary>Claude Desktop / Cursor format.</summary>
    public Dictionary<string, McpServerDto>? McpServers { get; set; }

    /// <summary>VS Code format.</summary>
    public Dictionary<string, McpServerDto>? Servers { get; set; }
}

internal sealed class McpServerDto
{
    /// <summary>"stdio" or "http" (also accepts "sse" / "streamable-http"); inferred from command/url when absent.</summary>
    public string? Type { get; set; }

    public string? Command { get; set; }

    public List<string>? Args { get; set; }

    public string? Cwd { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string>? Env { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public bool? Enabled { get; set; }

    public bool? Disabled { get; set; }

    public bool? RequireApproval { get; set; }

    public List<string>? AutoApprove { get; set; }

    public bool? TrustReadOnlyHints { get; set; }
}
