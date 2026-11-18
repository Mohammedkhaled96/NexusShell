using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusShell.Agents.Application.Abstractions.Attachments;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.Attachments;
using NexusShell.Agents.Infrastructure.Configuration;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Infrastructure.Tools;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>
/// The agent window's choices: model, expert, permission mode, working folder,
/// turned-off tools / skills / MCP servers, attachments, and user-added providers.
/// </summary>
public sealed class WorkspaceTests : IDisposable
{
    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    private AgentTestHost TwoExperts() => _host
        .Expert("alpha", "Alpha", "apples", "tools: [test]")
        .Expert("beta", "Beta", "bananas");

    private static IEnumerable<string> ToolNames(ChatOptions? options) =>
        options?.Tools?.Select(t => t.Name) ?? [];

    // ---------- Options applied to the configuration ----------

    [Fact]
    public async Task Chosen_model_is_used_by_every_expert_the_router_and_the_synthesizer()
    {
        TwoExperts();
        await using var services = _host.Build();
        var baseConfig = await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync();

        var config = ConversationOptionsApplier.Apply(baseConfig, new ConversationOptions { Model = "fake/pinned" });

        Assert.All(config.Experts, e => Assert.Equal("fake/pinned", e.Model));
        Assert.Equal("fake/pinned", config.Orchestration.SynthesizerModel);
        Assert.Equal("fake/pinned", config.Orchestration.Router.Model);
        var automatic = ConversationOptionsApplier.Apply(baseConfig, new ConversationOptions { Model = "auto" });
        Assert.Equal(baseConfig.Experts.Select(e => e.Model), automatic.Experts.Select(e => e.Model));
        Assert.Equal(baseConfig.Orchestration.SynthesizerModel, automatic.Orchestration.SynthesizerModel);
    }

    [Fact]
    public async Task Read_only_mode_blocks_changing_tools_and_scripts_and_tells_the_experts()
    {
        TwoExperts();
        await using var services = _host.Build();
        var baseConfig = await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync();

        var config = ConversationOptionsApplier.Apply(baseConfig, new ConversationOptions { Permission = PermissionMode.ReadOnly, WorkingDirectory = @"C:\work" });

        Assert.True(config.ToolPolicy.IsBlocked(ToolRisk.Destructive));
        Assert.True(config.ToolPolicy.IsBlocked(ToolRisk.Mutating));
        Assert.False(config.ToolPolicy.IsBlocked(ToolRisk.ReadOnly));
        Assert.False(config.Skills.EnableScripts);
        Assert.Contains("Read-only mode", config.SharedInstructions, StringComparison.Ordinal);
        Assert.Contains(@"C:\work", config.SharedInstructions, StringComparison.Ordinal);
        Assert.Equal(@"C:\work", config.WorkingDirectory);
    }

    [Fact]
    public async Task Auto_approve_edits_adds_mutating_tools_only()
    {
        TwoExperts();
        await using var services = _host.Build();
        var baseConfig = await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync();

        var config = ConversationOptionsApplier.Apply(baseConfig, new ConversationOptions { Permission = PermissionMode.AutoApproveEdits });

        Assert.Contains(ToolRisk.Mutating, config.ToolPolicy.AutoApprove);
        Assert.DoesNotContain(ToolRisk.Destructive, config.ToolPolicy.AutoApprove);
    }

    [Fact]
    public async Task Chosen_expert_answers_alone()
    {
        TwoExperts();
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>()
            .StartConversationAsync(null, new ConversationOptions { Expert = "beta" });
        var reply = await conversation.SendAsync("apples and bananas?");

        Assert.Equal("beta", Assert.Single(reply.Experts).ExpertId);
    }

    [Fact]
    public async Task Chosen_expert_can_delegate_to_experts_that_have_the_tools_it_lacks()
    {
        TwoExperts(); // alpha has the "test" tools, beta has none
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>()
            .StartConversationAsync(null, new ConversationOptions { Expert = "beta" });
        await conversation.SendAsync("write the file");

        var offered = _host.Chat.Calls.SelectMany(c => ToolNames(c.Options)).ToList();
        Assert.Contains("ask_alpha", offered);
        Assert.DoesNotContain("danger_tool", offered); // beta itself still has no such tool
        Assert.Contains(_host.Chat.Calls, c => ScriptedChatClient.Prompt(c.Messages, c.Options).Contains("a tool you do not have", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_only_mode_tells_the_user_how_to_allow_changes()
    {
        TwoExperts();
        await using var services = _host.Build();
        var baseConfig = await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync();

        var config = ConversationOptionsApplier.Apply(baseConfig, new ConversationOptions { Permission = PermissionMode.ReadOnly });

        Assert.Contains("/mode ask", config.SharedInstructions, StringComparison.Ordinal);
        Assert.Contains("Ask before changes", config.SharedInstructions, StringComparison.Ordinal);
    }

    // ---------- Options applied to a running conversation ----------

    [Fact]
    public async Task Read_only_mode_and_turned_off_tools_are_not_offered_to_the_model()
    {
        TwoExperts();
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = _host.Build();
        var service = services.GetRequiredService<INexusAgentService>();

        var normal = await service.StartConversationAsync();
        await normal.SendAsync("apples?");
        Assert.Contains("danger_tool", _host.Chat.Calls.SelectMany(c => ToolNames(c.Options)));

        _host.Chat.Calls.Clear();
        var readOnly = await service.StartConversationAsync(null, new ConversationOptions { Permission = PermissionMode.ReadOnly });
        await readOnly.SendAsync("apples?");
        var offered = _host.Chat.Calls.SelectMany(c => ToolNames(c.Options)).ToList();
        Assert.DoesNotContain("danger_tool", offered);
        Assert.Contains("leak_tool", offered); // read-only tools stay available

        _host.Chat.Calls.Clear();
        var noLeak = await service.StartConversationAsync(null, new ConversationOptions { DisabledTools = Set("leak_tool") });
        await noLeak.SendAsync("apples?");
        offered = _host.Chat.Calls.SelectMany(c => ToolNames(c.Options)).ToList();
        Assert.DoesNotContain("leak_tool", offered);
        Assert.Contains("danger_tool", offered);
    }

    [Fact]
    public async Task Changing_options_keeps_the_conversation_history()
    {
        TwoExperts();
        _host.Script((messages, _) => ScriptedChatClient.Text("seen: " + string.Join(" / ", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text))));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        await conversation.SendAsync("apples are red");
        await conversation.UpdateOptionsAsync(new ConversationOptions { Permission = PermissionMode.ReadOnly });
        var reply = await conversation.SendAsync("apples again?");

        Assert.Equal(PermissionMode.ReadOnly, conversation.Options.Permission);
        Assert.Contains("apples are red", string.Join("\n", _host.Chat.Calls.Last().Messages.Select(m => m.Text)), StringComparison.Ordinal);
        Assert.Contains("apples again?", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_options_do_not_rebuild_the_agent()
    {
        TwoExperts();
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = _host.Build();
        var options = new ConversationOptions { DisabledTools = Set("a", "b") };

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync(null, options);
        await conversation.UpdateOptionsAsync(options with { DisabledTools = Set("b", "a") });

        Assert.Equal(options.Fingerprint(), conversation.Options.Fingerprint());
    }

    // ---------- Catalog for the window ----------

    [Fact]
    public async Task Catalog_lists_models_experts_tools_skills_and_mcp_servers()
    {
        TwoExperts()
            .WriteBundled("agent.json", AgentTestHost.DefaultAgentJson.Replace("\"defaultModel\": \"test-model\"", "\"defaultModel\": \"test-model\", \"fallbackModels\": [\"second\"], \"models\": [\"extra\"]", StringComparison.Ordinal))
            .WriteBundled(Path.Combine("skills", "demo-skill", "SKILL.md"), "---\nname: demo-skill\ndescription: Shows how skills are listed.\n---\nDo it.")
            .WriteBundled("mcp.json", """{ "mcpServers": { "files": { "command": "files-server", "enabled": false } } }""");
        await using var services = _host.Build();

        var catalog = await services.GetRequiredService<INexusAgentService>().GetCatalogAsync();

        Assert.Equal(new[] { "fake/test-model", "fake/second", "fake/extra" }, catalog.Models.Select(m => m.Reference).ToArray());
        Assert.All(catalog.Models, m => Assert.True(m.IsAvailable));
        Assert.Equal(new[] { "alpha", "beta" }, catalog.Experts.Select(e => e.Id).Order().ToArray());
        Assert.Contains(catalog.Tools, t => t.Name == "danger_tool" && t.Risk == ToolRisk.Destructive && t.Category == "test");
        var skill = Assert.Single(catalog.Skills);
        Assert.Equal("demo-skill", skill.Name);
        Assert.Equal("Shows how skills are listed.", skill.Description);
        var mcp = Assert.Single(catalog.McpServers);
        Assert.Equal("files", mcp.Name);
        Assert.False(mcp.Enabled);
    }

    [Fact]
    public async Task Local_models_that_are_not_installed_are_listed_as_unavailable_with_the_install_command()
    {
        TwoExperts().WriteBundled("agent.json",
            """
            {
              "models": { "preference": ["local"] },
              "providers": { "local": { "kind": "ollama", "endpoint": "http://localhost:11434", "local": true, "defaultModel": "llama3.2", "models": ["qwen3"] } }
            }
            """);
        await using var services = _host.Build(s => s.AddSingleton<IModelDiscovery>(new FixedDiscovery("llama3.2:latest", "phi4-mini:latest")));

        var catalog = await services.GetRequiredService<INexusAgentService>().GetCatalogAsync();

        var byName = catalog.Models.ToDictionary(m => m.ModelId);
        Assert.True(byName["llama3.2"].IsAvailable);
        Assert.True(byName["phi4-mini:latest"].IsAvailable); // found on the server, not configured
        Assert.False(byName["qwen3"].IsAvailable);
        Assert.Contains("ollama pull qwen3", byName["qwen3"].Detail, StringComparison.Ordinal);
        Assert.True(catalog.Models.Take(2).All(m => m.IsAvailable)); // available models first
    }

    // ---------- Working folder ----------

    [Fact]
    public void Tool_paths_resolve_from_the_working_folder()
    {
        string folder = Path.Combine(_host.Root, "project");
        Directory.CreateDirectory(folder);

        Assert.Equal(Path.GetFullPath("x.txt"), ToolPaths.Resolve("x.txt")); // no folder chosen
        using (AgentRunScope.Begin(new AgentRunScope(null, new()) { WorkingDirectory = folder }))
        {
            Assert.Equal(Path.Combine(folder, "src", "a.cs"), ToolPaths.Resolve(@"src\a.cs"));
            Assert.Equal(folder, ToolPaths.Resolve("."));
            Assert.Equal(@"C:\other\b.txt", ToolPaths.Resolve(@"C:\other\b.txt"));
            Assert.Equal(folder, ToolPaths.WorkingDirectory);
        }
        Assert.Null(ToolPaths.WorkingDirectory);
    }

    // ---------- Attachments ----------

    private string SampleProject()
    {
        string folder = Path.Combine(_host.Root, "sample");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "README.md"), "# Sample\nA tiny project.");
        File.WriteAllText(Path.Combine(folder, "src", "Program.cs"), "Console.WriteLine(\"hi\");");
        File.WriteAllText(Path.Combine(folder, ".env"), "API_TOKEN=do-not-read-me");
        File.WriteAllBytes(Path.Combine(folder, "bin", "app.dll"), [0x4D, 0x5A, 0x00, 0x01]);
        File.WriteAllBytes(Path.Combine(folder, "data.bin.txt"), [0x41, 0x00, 0x42]);
        return folder;
    }

    [Fact]
    public async Task Attached_folder_includes_tree_and_text_files_but_never_credentials_or_binaries()
    {
        string folder = SampleProject();
        var reader = new FileAttachmentReader(NullLogger<FileAttachmentReader>.Instance);

        var bundle = await reader.ReadAsync([folder]);

        Assert.Contains("A tiny project.", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("src/", bundle.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-read-me", bundle.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("app.dll", bundle.Text, StringComparison.Ordinal); // build output is not even listed
        Assert.Equal(2, bundle.IncludedFiles);
    }

    [Fact]
    public async Task Attached_files_report_what_was_skipped()
    {
        string folder = SampleProject();
        var reader = new FileAttachmentReader(NullLogger<FileAttachmentReader>.Instance);

        var bundle = await reader.ReadAsync([
            Path.Combine(folder, "README.md"),
            Path.Combine(folder, ".env"),
            Path.Combine(folder, "data.bin.txt"),
            Path.Combine(folder, "missing.txt"),
        ]);

        Assert.Equal(1, bundle.IncludedFiles);
        Assert.Contains(bundle.Notes, n => n.StartsWith(".env", StringComparison.Ordinal) && n.Contains("credentials", StringComparison.Ordinal));
        Assert.Contains(bundle.Notes, n => n.StartsWith("data.bin.txt", StringComparison.Ordinal) && n.Contains("not a text file", StringComparison.Ordinal));
        Assert.Contains(bundle.Notes, n => n.StartsWith("missing.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Large_files_are_cut_to_the_limit()
    {
        string file = Path.Combine(_host.Root, "big.log");
        Directory.CreateDirectory(_host.Root);
        File.WriteAllText(file, new string('x', FileAttachmentReader.MaxFileChars + 5_000));
        var reader = new FileAttachmentReader(NullLogger<FileAttachmentReader>.Instance);

        var bundle = await reader.ReadAsync([file]);

        Assert.InRange(bundle.Text.Length, FileAttachmentReader.MaxFileChars, FileAttachmentReader.MaxFileChars + 500);
        Assert.Contains(bundle.Notes, n => n.Contains("only the first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attachments_reach_the_model_and_notes_reach_the_user()
    {
        TwoExperts();
        string folder = SampleProject();
        _host.Script((messages, _) => ScriptedChatClient.Text(messages.Any(m => m.Text.Contains("A tiny project.", StringComparison.Ordinal)) ? "I read the README" : "no attachment"));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("apples? explain this project", [Path.Combine(folder, "README.md"), Path.Combine(folder, ".env")]);

        Assert.Equal("I read the README", reply.Text);
        Assert.Contains(reply.AttachmentNotes, n => n.StartsWith(".env", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Chat.Calls.SelectMany(c => c.Messages), m => m.Text.Contains("do-not-read-me", StringComparison.Ordinal));
    }

    // ---------- Providers added by the user ----------

    private UserAgentSettingsStore Store(IAgentConfigurationProvider configuration) =>
        new(new NexusAgentPathsOptions { BundledDirectory = _host.Bundled, UserDirectory = _host.User, ScaffoldUserDirectory = false },
            configuration, NullLogger<UserAgentSettingsStore>.Instance);

    [Fact]
    public async Task Added_provider_is_saved_to_the_user_file_and_loaded_without_any_secret()
    {
        TwoExperts();
        await using var services = _host.Build();
        var configuration = services.GetRequiredService<IAgentConfigurationProvider>();
        var store = Store(configuration);

        await store.SaveProviderAsync(new ProviderSettings
        {
            Id = "my-cloud",
            Kind = "openai-compatible",
            Endpoint = "https://api.example.com/v1",
            ApiKeyEnv = "MY_CLOUD_API_KEY",
            DefaultModel = "big-model",
            Models = ["big-model", "small-model"],
            Prefer = true,
        });

        var config = await configuration.GetConfigurationAsync();
        var provider = config.Providers["my-cloud"];
        Assert.Equal(ProviderKind.OpenAICompatible, provider.Kind);
        Assert.Equal("MY_CLOUD_API_KEY", provider.ApiKey!.Name);
        Assert.Equal("big-model", provider.DefaultModel);
        Assert.Contains("small-model", provider.KnownModels);
        Assert.Equal("my-cloud", config.ModelPreference[0]);
        Assert.Contains("fake", config.ModelPreference); // previous order kept after it

        string json = File.ReadAllText(Path.Combine(_host.User, "agent.json"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("MY_CLOUD_API_KEY", document.RootElement.GetProperty("providers").GetProperty("my-cloud").GetProperty("apiKeyEnv").GetString());
    }

    // Key-shaped values are built at runtime so no secret-looking literal lives in the source.
    [Theory]
    [InlineData("sk-", "x")]     // OpenAI style prefix
    [InlineData("AIza", "x")]    // Google style prefix
    [InlineData("gsk_", "x")]    // Groq style prefix
    [InlineData("", "Ab1")]      // long mix of upper case, lower case and digits
    public void A_key_typed_where_the_variable_name_goes_is_refused(string prefix, string filler)
    {
        string value = prefix + string.Concat(Enumerable.Repeat(filler, 30 / filler.Length + 1));
        var error = Assert.Throws<ArgumentException>(() => UserAgentSettingsStore.Validate(new ProviderSettings
        {
            Id = "x",
            Kind = "openai-compatible",
            Endpoint = "https://api.example.com/v1",
            ApiKeyEnv = value,
        }));
        Assert.Contains("looks like an API key", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://api.example.com/v1", false, "https")]
    [InlineData("https://user:pass@api.example.com/v1", false, "key")]
    [InlineData("https://api.example.com/v1?key=abc", false, "key")]
    [InlineData("ftp://example.com", true, "https://")]
    public void Unsafe_addresses_are_refused(string endpoint, bool local, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => UserAgentSettingsStore.Validate(new ProviderSettings
        {
            Id = "x",
            Kind = "openai-compatible",
            Endpoint = endpoint,
            ApiKeyEnv = "X_API_KEY",
            Local = local,
        }));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_server_on_http_without_key_is_accepted()
    {
        var (id, kind, endpoint) = UserAgentSettingsStore.Validate(new ProviderSettings
        {
            Id = "My Box",
            Kind = "openai-compatible",
            Endpoint = "http://192.168.1.20:8000/v1",
            Local = true,
        });

        Assert.Equal("my-box", id);
        Assert.Equal(ProviderKind.OpenAICompatible, kind);
        Assert.Equal("http", endpoint!.Scheme);
    }

    [Fact]
    public async Task Model_added_to_a_bundled_provider_keeps_the_bundled_settings()
    {
        TwoExperts();
        await using var services = _host.Build();
        var configuration = services.GetRequiredService<IAgentConfigurationProvider>();

        await Store(configuration).AddModelAsync("fake", "brand-new-model");

        var provider = (await configuration.GetConfigurationAsync()).Providers["fake"];
        Assert.Equal("test-model", provider.DefaultModel);          // from the bundled file
        Assert.Equal(new Uri("https://fake.invalid/v1"), provider.Endpoint);
        Assert.Contains("brand-new-model", provider.KnownModels);   // from the user file
    }

    [Fact]
    public async Task Removing_a_bundled_provider_turns_it_off()
    {
        TwoExperts();
        await using var services = _host.Build();
        var configuration = services.GetRequiredService<IAgentConfigurationProvider>();

        await Store(configuration).RemoveProviderAsync("fake");

        Assert.False((await configuration.GetConfigurationAsync()).Providers["fake"].Enabled);
    }

    // ---------- Model discovery and local models without tools ----------

    [Fact]
    public void Model_lists_are_parsed_for_ollama_and_openai_compatible_servers()
    {
        using var ollama = JsonDocument.Parse("""{ "models": [ { "name": "llama3.2:latest" }, { "name": "qwen3:8b" } ] }""");
        using var openai = JsonDocument.Parse("""{ "object": "list", "data": [ { "id": "models/gemini-3.5-flash" }, { "id": "gpt-x" }, { "id": "gpt-x" } ] }""");

        Assert.Equal(new[] { "llama3.2:latest", "qwen3:8b" }, ModelDiscovery.Parse(ProviderKind.Ollama, ollama.RootElement));
        Assert.Equal(new[] { "gemini-3.5-flash", "gpt-x" }, ModelDiscovery.Parse(ProviderKind.OpenAICompatible, openai.RootElement));
    }

    [Fact]
    public async Task A_model_that_cannot_call_tools_answers_without_them()
    {
        var provider = new ProviderProfile { Id = "local", Kind = ProviderKind.Ollama, IsLocal = true, DefaultModel = "gemma3" };
        var inner = new ScriptedChatClient((_, options) => options?.Tools is { Count: > 0 }
            ? throw new HttpRequestException("registry.ollama.ai/library/gemma3:latest does not support tools", null, HttpStatusCode.BadRequest)
            : ScriptedChatClient.Text("plain answer"));
        using var client = new ProviderErrorChatClient(inner, provider, "gemma3");
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "some_tool")] };

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "again")], options);

        Assert.Equal("plain answer", first.Text);
        Assert.Equal("plain answer", second.Text);
        Assert.Equal(3, inner.Calls.Count); // one refused call, then tools are no longer sent
        Assert.NotNull(options.Tools);       // the caller's options are not modified
    }

    [Fact]
    public void Bundled_configuration_offers_local_servers_in_the_automatic_order()
    {
        var config = new FileAgentConfigurationProvider(
            new NexusAgentPathsOptions
            {
                BundledDirectory = Path.Combine(AppContext.BaseDirectory, "Agent"),
                UserDirectory = Path.Combine(_host.Root, "empty-user"),
                ScaffoldUserDirectory = false,
            },
            NullLogger<FileAgentConfigurationProvider>.Instance).Load();

        foreach (string local in new[] { "ollama", "lmstudio", "llamacpp", "vllm", "jan" })
        {
            Assert.True(config.Providers[local].IsLocal, local);
            Assert.Contains(local, config.ModelPreference);
        }
        Assert.Contains("qwen3", config.Providers["ollama"].KnownModels);
        Assert.DoesNotContain("qwen3", config.Providers["ollama"].CandidateModels); // suggestions are never auto-picked
    }

    private sealed class FixedDiscovery(params string[] models) : IModelDiscovery
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(ProviderProfile provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(models);
    }
}
