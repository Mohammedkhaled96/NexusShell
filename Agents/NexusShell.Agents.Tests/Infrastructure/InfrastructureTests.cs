using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Infrastructure.Configuration;
using NexusShell.Agents.Infrastructure.Mcp;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Infrastructure.Secrets;
using NexusShell.Agents.Infrastructure.Tools.FileSystem;
using NexusShell.Agents.Infrastructure.Tools.Web;

namespace NexusShell.Agents.Tests.Infrastructure;

public class FrontmatterDocumentTests
{
    [Fact]
    public void Parses_scalars_lists_blocks_and_body()
    {
        var doc = FrontmatterDocument.Parse(
            "---\r\n" +
            "name: \"Shell Expert\"\r\n" +
            "keywords: [powershell, 'cmd', c#]\r\n" +
            "tools:\r\n  - shell\r\n  - filesystem\r\n" +
            "description: >\r\n  first line\r\n  second line\r\n" +
            "routable: false\r\n" +
            "temperature: 0.25\r\n" +
            "# a comment\r\n" +
            "---\r\n" +
            "Body text\r\n");

        Assert.Equal("Shell Expert", doc.GetString("name"));
        Assert.Equal(new[] { "powershell", "cmd", "c#" }, doc.GetList("keywords"));
        Assert.Equal(new[] { "shell", "filesystem" }, doc.GetList("tools"));
        Assert.Equal("first line second line", doc.GetString("description"));
        Assert.False(doc.GetBool("routable"));
        Assert.Equal(0.25, doc.GetDouble("temperature"));
        Assert.Equal("Body text", doc.Body);
    }

    [Fact]
    public void Missing_closing_marker_is_an_error() =>
        Assert.Throws<FormatException>(() => FrontmatterDocument.Parse("---\nname: x\nbody"));

    [Fact]
    public void Text_without_frontmatter_is_all_body()
    {
        var doc = FrontmatterDocument.Parse("just instructions");
        Assert.False(doc.HasFrontmatter);
        Assert.Equal("just instructions", doc.Body);
    }
}

public class BundledConfigurationTests
{
    private static FileAgentConfigurationProvider Provider(string user) => new(
        new NexusAgentPathsOptions
        {
            BundledDirectory = Path.Combine(AppContext.BaseDirectory, "Agent"),
            UserDirectory = user,
            ScaffoldUserDirectory = false,
        },
        NullLogger<FileAgentConfigurationProvider>.Instance);

    [Fact]
    public void Bundled_defaults_load_without_warnings()
    {
        var config = Provider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Load();

        Assert.Empty(config.Warnings);
        Assert.Equal(7, config.Experts.Count);
        Assert.Contains(config.Experts, e => e.Id == "shell-expert" && e.Tools.Contains("shell"));
        Assert.False(config.FindExpert("reviewer")!.Routable);
        Assert.Equal(new[] { "reviewer" }, config.FindExpert("code-expert")!.SubAgents);
        Assert.True(config.Providers["ollama"].IsLocal);
        Assert.Equal("ANTHROPIC_API_KEY", config.Providers["anthropic"].ApiKey!.Name);
        Assert.Contains("screen readers", config.SharedInstructions, StringComparison.Ordinal);
        Assert.All(config.Providers.Values, p => Assert.True(p.ApiKey is null || SecretReference.IsValidName(p.ApiKey.Name)));
    }

    [Fact]
    public void User_files_override_bundled_defaults()
    {
        string user = Path.Combine(Path.GetTempPath(), "nexus-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(user, "experts"));
        try
        {
            File.WriteAllText(Path.Combine(user, "agent.json"), """{ "orchestration": { "mode": "coordinator", "router": { "topK": 3 } } }""");
            File.WriteAllText(Path.Combine(user, "experts", "shell-expert.md"), "---\nenabled: false\n---\n");

            var config = Provider(user).Load();

            Assert.Empty(config.Warnings);
            Assert.Equal(NexusShell.Agents.Domain.Orchestration.OrchestrationMode.Coordinator, config.Orchestration.Mode);
            Assert.Equal(3, config.Orchestration.Router.TopK);
            Assert.Equal(0.25, config.Orchestration.Router.MinScore); // inherited from bundled
            Assert.False(config.FindExpert("shell-expert")!.Enabled);
            Assert.Contains("PowerShell", config.FindExpert("shell-expert")!.Instructions, StringComparison.Ordinal); // body inherited
        }
        finally
        {
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public void Literal_secrets_in_configuration_are_rejected()
    {
        string user = Path.Combine(Path.GetTempPath(), "nexus-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(user);
        try
        {
            File.WriteAllText(Path.Combine(user, "agent.json"),
                """{ "providers": { "openai": { "kind": "openai", "apiKey": "literal-key-value" } } }""");
            File.WriteAllText(Path.Combine(user, "mcp.json"),
                """{ "mcpServers": { "gh": { "command": "npx", "env": { "GITHUB_TOKEN": "literal-token-value" } } } }""");

            var config = Provider(user).Load();

            Assert.DoesNotContain("openai", config.Providers.Keys);
            Assert.Contains(config.Warnings, w => w.Contains("DISABLED", StringComparison.Ordinal) && w.Contains("openai", StringComparison.Ordinal));
            Assert.False(Assert.Single(config.McpServers).Enabled);
            Assert.Contains(config.Warnings, w => w.Contains("GITHUB_TOKEN", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public void Remote_http_provider_is_rejected_but_local_http_is_allowed()
    {
        var warnings = new List<string>();
        var providers = FileAgentConfigurationProvider.MapProviders(new()
        {
            ["remote"] = new() { Kind = "openai-compatible", Endpoint = "http://api.remote.invalid/v1", ApiKeyEnv = "X_KEY" },
            ["lan"] = new() { Kind = "openai-compatible", Endpoint = "http://192.168.1.20:8000/v1", Local = true },
        }, warnings);

        Assert.DoesNotContain("remote", providers.Keys);
        Assert.Contains("lan", providers.Keys);
        Assert.Single(warnings);
    }
}

public class SecretStoreTests
{
    [Fact]
    public async Task Process_scope_round_trip_never_logs_or_persists_elsewhere()
    {
        var store = new EnvironmentVariableSecretStore(new SecretStoreOptions { PersistTarget = EnvironmentVariableTarget.Process }, NullLogger<EnvironmentVariableSecretStore>.Instance);
        var reference = new SecretReference("NEXUS_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant());

        Assert.Null(store.GetSecret(reference));
        await store.SetSecretAsync(reference, "  value-123  ");
        Assert.Equal("value-123", store.GetSecret(reference));
        Assert.True(((NexusShell.Agents.Application.Abstractions.Secrets.ISecretStore)store).HasSecret(reference));

        await store.RemoveSecretAsync(reference);
        Assert.Null(store.GetSecret(reference));
    }

    [Fact]
    public async Task Multi_line_values_are_rejected()
    {
        var store = new EnvironmentVariableSecretStore(new SecretStoreOptions { PersistTarget = EnvironmentVariableTarget.Process }, NullLogger<EnvironmentVariableSecretStore>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetSecretAsync(new SecretReference("NEXUS_X"), "a\nb"));
    }
}

public class SecretRedactorTests
{
    private static SecretRedactor Redactor(params (string Name, string Value)[] variables) =>
        new(() => variables.Select(v => new KeyValuePair<string, string>(v.Name, v.Value)), TimeProvider.System);

    [Fact]
    public void Redacts_values_of_sensitive_variables()
    {
        var redactor = Redactor(("OPENAI_API_KEY", "redaction-test-value-a1"), ("GITHUB_TOKEN", "redaction-test-value-b2"));
        string result = redactor.Redact("key=redaction-test-value-a1 and redaction-test-value-b2!");
        Assert.Equal("key=[REDACTED:OPENAI_API_KEY] and [REDACTED:GITHUB_TOKEN]!", result);
    }

    [Fact]
    public void Ignores_non_sensitive_short_and_path_values()
    {
        var redactor = Redactor(("PATH", "C:\\Windows\\System32"), ("SESSIONNAME", "Console"), ("GIT_ASKPASS_KEY", Path.Combine(Path.GetTempPath(), "askpass.exe")), ("NUMBER_KEY", "123456789"));
        const string text = "C:\\Windows\\System32 Console 123456789";
        Assert.Equal(text, redactor.Redact(text));
    }
}

public class ProviderAndToolSafetyTests
{
    [Theory]
    [InlineData("https://res.openai.azure.com", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/openai", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/openai/v1/", "https://res.openai.azure.com/openai/v1/")]
    public void Azure_endpoint_is_normalized_to_v1(string input, string expected) =>
        Assert.Equal(expected, OpenAICompatibleProviderAdapter.NormalizeAzureEndpoint(new Uri(input)).ToString());

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void Web_tool_refuses_private_addresses(string ip, bool blocked) =>
        Assert.Equal(blocked, WebToolProvider.IsNonPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task Web_tool_refuses_non_http_and_localhost()
    {
        Assert.NotNull(await WebToolProvider.CheckPublicAsync(new Uri("file:///c:/windows/win.ini"), CancellationToken.None));
        Assert.NotNull(await WebToolProvider.CheckPublicAsync(new Uri("http://127.0.0.1:11434/api/tags"), CancellationToken.None));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\.ssh\\id_ed25519", true)]
    [InlineData("C:\\repo\\.env", true)]
    [InlineData("C:\\repo\\.env.production", true)]
    [InlineData("C:\\certs\\server.pfx", true)]
    [InlineData("C:\\repo\\src\\Program.cs", false)]
    public void Credential_files_are_never_read(string path, bool blocked)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(blocked, FileSystemToolProvider.IsBlocked(path));
    }

    [Theory]
    [InlineData("github", "create_issue", "mcp_github_create_issue")]
    [InlineData("my server", "do.thing!", "mcp_my_server_do_thing")]
    public void Mcp_tool_names_are_provider_safe(string server, string tool, string expected) =>
        Assert.Equal(expected, McpToolNaming.Create(server, tool));

    [Fact]
    public void Mcp_tool_names_are_capped_at_64_chars() =>
        Assert.Equal(64, McpToolNaming.Create(new string('s', 40), new string('t', 40)).Length);
}
