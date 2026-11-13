using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusShell.Agents.Application;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Skills;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Infrastructure.Configuration;
using NexusShell.Agents.Infrastructure.Mcp;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Infrastructure.Secrets;
using NexusShell.Agents.Infrastructure.Skills;
using NexusShell.Agents.Infrastructure.Tools.FileSystem;
using NexusShell.Agents.Infrastructure.Tools.Shell;
using NexusShell.Agents.Infrastructure.Tools.SystemInfo;
using NexusShell.Agents.Infrastructure.Tools.Web;

namespace NexusShell.Agents.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Composition root of the agent framework: registers the Application layer and
    /// every Infrastructure adapter. Hosts only need this call (plus, optionally,
    /// their own <see cref="Application.Abstractions.Approval.IToolApprovalHandler"/>
    /// and extra tools via <see cref="AddAgentToolProvider{TProvider}"/>).
    /// </summary>
    public static IServiceCollection AddNexusAgents(this IServiceCollection services, Action<NexusAgentPathsOptions>? configurePaths = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var paths = new NexusAgentPathsOptions();
        configurePaths?.Invoke(paths);
        services.TryAddSingleton(paths);

        services.AddNexusAgentsApplication();

        // Configuration & secrets
        services.TryAddSingleton<IAgentConfigurationProvider, FileAgentConfigurationProvider>();
        services.TryAddSingleton(new SecretStoreOptions());
        services.TryAddSingleton<ISecretStore, EnvironmentVariableSecretStore>();
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();

        // HTTP
        services.TryAddSingleton<ThoughtSignatureCache>();
        services.TryAddTransient<ThoughtSignatureHandler>();
        services.AddHttpClient(HttpClientNames.Llm, c => c.Timeout = TimeSpan.FromMinutes(5))
            .AddHttpMessageHandler<ThoughtSignatureHandler>();
        services.AddHttpClient(HttpClientNames.Probe, c => c.Timeout = TimeSpan.FromSeconds(2));
        services.AddHttpClient(HttpClientNames.WebTool, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusShell-Agent/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // redirects are followed manually and re-validated
                AutomaticDecompression = DecompressionMethods.All,
                ConnectCallback = PublicOnlyConnectCallback.ConnectAsync,
            });

        // Model providers (Strategy per protocol)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderAdapter, OpenAICompatibleProviderAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderAdapter, AnthropicProviderAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderAdapter, OllamaProviderAdapter>());
        services.TryAddSingleton<IChatClientFactory, ChatClientFactory>();
        services.TryAddSingleton<IProviderAvailability, ProviderAvailability>();

        // MCP + skills
        services.TryAddSingleton<IMcpToolProvider, McpToolProvider>();
        services.TryAddSingleton<SkillScriptRunner>();
        services.TryAddSingleton<ISkillsProviderFactory, FileSkillsProviderFactory>();

        // Built-in tools (one provider per category)
        services.AddAgentToolProvider<SystemInfoToolProvider>();
        services.AddAgentToolProvider<FileSystemToolProvider>();
        services.AddAgentToolProvider<ShellToolProvider>();
        services.AddAgentToolProvider<WebToolProvider>();

        return services;
    }

    /// <summary>Extension point: contribute a tool set (category) to every expert that selects it.</summary>
    public static IServiceCollection AddAgentToolProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IAgentToolProvider
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolProvider, TProvider>());
        return services;
    }
}
