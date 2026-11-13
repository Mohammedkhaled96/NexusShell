using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure;
using NexusShell.Agents.Infrastructure.Secrets;

namespace NexusShell.Agents.Tests.Fakes;

/// <summary>
/// Builds the REAL agent composition root (Application + Infrastructure) in a temp
/// folder, replacing only the edges: model calls (scripted), provider
/// availability, approval UI and secret persistence (process scope only).
/// </summary>
internal sealed class AgentTestHost : IDisposable
{
    public const string DefaultAgentJson =
        """
        {
          "orchestration": {
            "mode": "moe",
            "router": { "strategy": "keyword", "topK": 2, "minScore": 0.2 },
            "defaultExpert": "alpha",
            "coordinatorExpert": "alpha",
            "sequence": ["alpha", "beta"]
          },
          "models": { "preference": ["fake"] },
          "providers": {
            "fake": { "kind": "openai-compatible", "endpoint": "https://fake.invalid/v1", "defaultModel": "test-model" }
          }
        }
        """;

    public AgentTestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "nexus-agent-tests", Guid.NewGuid().ToString("N"));
        Bundled = Path.Combine(Root, "bundled");
        User = Path.Combine(Root, "user");
        Directory.CreateDirectory(Path.Combine(Bundled, "experts"));
        Directory.CreateDirectory(Path.Combine(User, "experts"));
        WriteBundled("agent.json", DefaultAgentJson);
    }

    public string Root { get; }

    public string Bundled { get; }

    public string User { get; }

    public ScriptedChatClient Chat { get; private set; } = new((_, _) => ScriptedChatClient.Text("unscripted"));

    public RecordingApprovalHandler Approvals { get; } = new(ToolApprovalDecision.ApproveOnce);

    public TestToolProvider Tools { get; } = new();

    public AgentTestHost Script(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> respond)
    {
        Chat = new ScriptedChatClient(respond);
        return this;
    }

    public AgentTestHost WriteBundled(string relativePath, string content)
    {
        string path = Path.Combine(Bundled, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public AgentTestHost WriteUser(string relativePath, string content)
    {
        string path = Path.Combine(User, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public AgentTestHost Expert(string id, string name, string keywords, string extraFrontmatter = "", string body = "Answer the user.") =>
        WriteBundled(Path.Combine("experts", id + ".md"),
            $"""
            ---
            name: {name}
            description: {name} handles {keywords} questions.
            model: fake/test-model
            keywords: [{keywords}]
            {extraFrontmatter}
            ---
            {body}
            """);

    public ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientFactory>(new DelegatingFactory(() => Chat));
        services.AddSingleton<IProviderAvailability>(new AlwaysAvailable());
        services.AddSingleton<IToolApprovalHandler>(Approvals);
        services.AddSingleton(new SecretStoreOptions { PersistTarget = EnvironmentVariableTarget.Process });
        services.AddSingleton<IAgentToolProvider>(Tools);
        configure?.Invoke(services);
        services.AddNexusAgents(p =>
        {
            p.BundledDirectory = Bundled;
            p.UserDirectory = User;
            p.ScaffoldUserDirectory = false;
        });
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class DelegatingFactory(Func<IChatClient> current) : IChatClientFactory
    {
        public IChatClient CreateChatClient(ProviderProfile provider, string modelId) => current();
    }

    private sealed class AlwaysAvailable : IProviderAvailability
    {
        public Task<ProviderStatus> GetStatusAsync(ProviderProfile provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(provider.Id, true, "test"));
    }
}

internal sealed class RecordingApprovalHandler(ToolApprovalDecision decision) : IToolApprovalHandler
{
    public ToolApprovalDecision Decision { get; set; } = decision;

    public ConcurrentQueue<ToolApprovalRequest> Requests { get; } = new();

    public Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Decision);
    }
}

/// <summary>Category "test": a destructive tool (needs approval) and a read-only tool that leaks a secret.</summary>
internal sealed class TestToolProvider : IAgentToolProvider
{
    public const string SecretVariable = "NEXUS_AGENT_TEST_TOKEN";

    public ConcurrentQueue<string> Deleted { get; } = new();

    public string Category => "test";

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new AgentToolDescriptor(
            AIFunctionFactory.Create((string target) =>
            {
                Deleted.Enqueue(target);
                return $"deleted {target}";
            }, "danger_tool", "Deletes something."),
            ToolRisk.Destructive,
            Category);

        yield return new AgentToolDescriptor(
            AIFunctionFactory.Create(() => "the token is " + Environment.GetEnvironmentVariable(SecretVariable), "leak_tool", "Prints configuration."),
            ToolRisk.ReadOnly,
            Category);
    }
}
