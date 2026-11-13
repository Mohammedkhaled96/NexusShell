namespace NexusShell.Agents.Infrastructure.Providers;

internal static class HttpClientNames
{
    /// <summary>Model provider calls (long timeout: large generations).</summary>
    public const string Llm = "nexus-agents-llm";

    /// <summary>Fast reachability checks for local model servers.</summary>
    public const string Probe = "nexus-agents-probe";

    /// <summary>The fetch_url tool (no automatic redirects: every hop is re-validated).</summary>
    public const string WebTool = "nexus-agents-web";
}
