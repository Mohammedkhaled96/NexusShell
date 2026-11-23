using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Application.Connectors;
using NexusShell.Agents.Domain.Connectors;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.Providers;

namespace NexusShell.Agents.Infrastructure.Connectors.GitHub;

/// <summary>GitHub through its REST API with a personal access token kept in GITHUB_TOKEN.</summary>
internal sealed class GitHubConnector(ISecretStore secrets, IHttpClientFactory httpClientFactory, IBrowserLauncher browser) : IConnector
{
    private const string TokenPage = "https://github.com/settings/personal-access-tokens/new";

    public const string TokenVariable = "GITHUB_TOKEN";

    public static ConnectorDefinition Info { get; } = new()
    {
        Id = "github",
        Name = "GitHub",
        Description = "Your repositories, issues, pull requests, notifications and search; create issues and comments (these ask first).",
        Auth = ConnectorAuthKind.Token,
        ToolCategories = [GitHubToolProvider.CategoryName],
        Fields = [new(TokenVariable, "Personal access token", true, "A fine-grained token limited to the repositories the agent may use.")],
        SetupUrl = TokenPage,
        SetupLinks = [new("Create a token", TokenPage)],
        SetupSteps =
        [
            "Open the setup page and create a fine-grained personal access token.",
            "Choose the repositories the agent may use, and the permissions: Issues read and write, Pull requests read, Contents read.",
            "Paste the token here and press Save.",
            "Press Connect to check it. (Notifications need a classic token with the notifications scope; everything else works with a fine-grained token.)",
        ],
        Abilities =
        [
            new("List your repositories, issues and pull requests", false),
            new("Read issues and pull requests with their comments", false),
            new("Search GitHub and read your notifications", false),
            new("Create issues and comment", true),
        ],
    };

    public ConnectorDefinition Definition => Info;

    public ConnectorState CurrentState => secrets.HasSecret(new SecretReference(TokenVariable)) ? ConnectorState.Connected : ConnectorState.NeedsSetup;

    public Task<ConnectorStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState == ConnectorState.Connected) return TestAsync(cancellationToken);
        browser.Open(new Uri(TokenPage));
        return Task.FromResult(new ConnectorStatus(ConnectorState.NeedsSetup,
            "Your browser opened GitHub's token page. Create a fine-grained token, paste it in the Personal access token box, press Save, then press Connect again."));
    }

    public async Task<ConnectorStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState != ConnectorState.Connected) return new ConnectorStatus(ConnectorState.NeedsSetup, "Paste a personal access token first.");
        var user = await GitHubToolProvider.CallAsync(httpClientFactory, secrets, HttpMethod.Get, "user", null, cancellationToken).ConfigureAwait(false);
        string login = user?.Str("login") ?? string.Empty;
        return new ConnectorStatus(ConnectorState.Connected, $"Connected to GitHub as {login}.", login);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        secrets.RemoveSecretAsync(new SecretReference(TokenVariable), cancellationToken);
}

/// <summary>Category "github": reading is free; creating issues and comments always asks.</summary>
internal sealed partial class GitHubToolProvider(ISecretStore secrets, IHttpClientFactory httpClientFactory) : IAgentToolProvider
{
    public const string CategoryName = "github";

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();

    public string Category => CategoryName;

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(MyRepositoriesAsync, "github_my_repositories", "List the user's GitHub repositories, most recently updated first."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(IssuesAsync, "github_list_issues", "List issues of a repository (owner/name)."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(PullRequestsAsync, "github_list_pull_requests", "List pull requests of a repository (owner/name)."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(GetIssueAsync, "github_get_issue", "Read an issue or pull request with its comments."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(SearchAsync, "github_search", "Search GitHub issues and pull requests, repositories or code."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(NotificationsAsync, "github_notifications", "List the user's unread GitHub notifications."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(CreateIssueAsync, "github_create_issue", "Create an issue in a repository. The user approves it first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(CommentAsync, "github_comment", "Comment on an issue or pull request. The user approves it first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
    }

    internal static Task<JsonElement?> CallAsync(IHttpClientFactory factory, ISecretStore secrets, HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        ConnectorHttp.SendAsync(
            factory.CreateClient(HttpClientNames.Connectors), "GitHub", method, "https://api.github.com/" + path,
            (_, _) => Task.FromResult(secrets.GetSecret(new SecretReference(GitHubConnector.TokenVariable))
                ?? throw new ConnectorException("GitHub is not connected. Open Connectors and paste a personal access token.")),
            body, cancellationToken,
            new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json", ["X-GitHub-Api-Version"] = "2022-11-28" });

    private Task<JsonElement?> CallAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        CallAsync(httpClientFactory, secrets, method, path, body, cancellationToken);

    private static string Repo(string repository)
    {
        string value = repository.Trim().Replace("https://github.com/", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('/');
        if (!RepositoryPattern().IsMatch(value)) throw new ConnectorException($"\"{repository}\" is not a repository. Use owner/name, for example microsoft/vscode.");
        return value;
    }

    private static string Clip(string? text, int max) => text is null ? string.Empty : ConnectorHttp.Clip(text, max);

    private async Task<string> MyRepositoriesAsync([Description("Maximum repositories (1-50).")] int maxResults = 20, CancellationToken cancellationToken = default)
    {
        try
        {
            var repos = await CallAsync(HttpMethod.Get, $"user/repos?sort=updated&per_page={Math.Clamp(maxResults, 1, 50)}", null, cancellationToken).ConfigureAwait(false);
            var sb = new StringBuilder();
            foreach (var r in repos.Items())
            {
                sb.Append("- ").Append(r.Str("full_name")).Append(r.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True ? " (private)" : string.Empty)
                  .Append(" | updated ").Append(r.Str("updated_at")).Append(" | ").AppendLine(r.Str("description"));
            }
            return sb.Length == 0 ? "No repositories." : sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> IssuesAsync(
        [Description("Repository as owner/name.")] string repository,
        [Description("open, closed or all.")] string state = "open",
        [Description("Maximum issues (1-50).")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await CallAsync(HttpMethod.Get, $"repos/{Repo(repository)}/issues?state={State(state)}&per_page={Math.Clamp(maxResults, 1, 50)}", null, cancellationToken).ConfigureAwait(false);
            var issues = items.Items().Where(i => !i.TryGetProperty("pull_request", out _)).ToList();
            if (issues.Count == 0) return $"No {State(state)} issues in {repository}.";
            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var i in issues) sb.Append("- #").Append(i.GetProperty("number").GetInt32()).Append(' ').Append(i.Str("title")).Append(" (by ").Append(i.Obj("user").Str("login")).Append(", ").Append(i.Str("state")).AppendLine(")");
            return sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> PullRequestsAsync(
        [Description("Repository as owner/name.")] string repository,
        [Description("open, closed or all.")] string state = "open",
        [Description("Maximum pull requests (1-50).")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await CallAsync(HttpMethod.Get, $"repos/{Repo(repository)}/pulls?state={State(state)}&per_page={Math.Clamp(maxResults, 1, 50)}", null, cancellationToken).ConfigureAwait(false);
            var pulls = items.Items().ToList();
            if (pulls.Count == 0) return $"No {State(state)} pull requests in {repository}.";
            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var p in pulls)
            {
                sb.Append("- #").Append(p.GetProperty("number").GetInt32()).Append(' ').Append(p.Str("title")).Append(" (by ").Append(p.Obj("user").Str("login"))
                  .Append(", ").Append(p.Obj("head").Str("ref")).Append(" into ").Append(p.Obj("base").Str("ref")).AppendLine(")");
            }
            return sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> GetIssueAsync(
        [Description("Repository as owner/name.")] string repository,
        [Description("Issue or pull request number.")] int number,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string repo = Repo(repository);
            var issue = await CallAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}", null, cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorException("Not found.");
            var comments = await CallAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}/comments?per_page=30", null, cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine()
                .Append('#').Append(number).Append(' ').AppendLine(issue.Str("title"))
                .Append(issue.TryGetProperty("pull_request", out _) ? "Pull request" : "Issue").Append(", ").Append(issue.Str("state"))
                .Append(", by ").Append(issue.Obj("user").Str("login")).Append(", ").AppendLine(issue.Str("created_at"))
                .AppendLine().AppendLine(Clip(issue.Str("body"), 8_000));
            foreach (var c in comments.Items())
                sb.AppendLine().Append("Comment by ").Append(c.Obj("user").Str("login")).Append(" (").Append(c.Str("created_at")).AppendLine("):").AppendLine(Clip(c.Str("body"), 3_000));
            return ConnectorHttp.Clip(sb.ToString(), 30_000);
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> SearchAsync(
        [Description("GitHub search query, e.g. \"repo:owner/name is:open label:bug\" or \"language:csharp mcp\".")] string query,
        [Description("issues (also pull requests), repositories or code.")] string kind = "issues",
        [Description("Maximum results (1-30).")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string type = kind.Trim().ToLowerInvariant() switch { "repositories" or "repos" or "repository" => "repositories", "code" => "code", _ => "issues" };
            var result = await CallAsync(HttpMethod.Get, $"search/{type}?q={Uri.EscapeDataString(query)}&per_page={Math.Clamp(maxResults, 1, 30)}", null, cancellationToken).ConfigureAwait(false);
            var items = result is { } r ? r.Items("items").ToList() : [];
            if (items.Count == 0) return $"No GitHub {type} match \"{query}\".";
            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var i in items)
            {
                sb.AppendLine(type switch
                {
                    "repositories" => $"- {i.Str("full_name")}: {i.Str("description")}",
                    "code" => $"- {i.Obj("repository").Str("full_name")}: {i.Str("path")}",
                    _ => $"- {i.Str("html_url")} {i.Str("title")} ({i.Str("state")})",
                });
            }
            return sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> NotificationsAsync([Description("Maximum notifications (1-50).")] int maxResults = 20, CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await CallAsync(HttpMethod.Get, $"notifications?per_page={Math.Clamp(maxResults, 1, 50)}", null, cancellationToken).ConfigureAwait(false);
            var list = items.Items().ToList();
            if (list.Count == 0) return "No unread GitHub notifications.";
            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var n in list)
                sb.Append("- ").Append(n.Obj("repository").Str("full_name")).Append(": ").Append(n.Obj("subject").Str("title")).Append(" (").Append(n.Str("reason")).AppendLine(")");
            return sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> CreateIssueAsync(
        [Description("Repository as owner/name.")] string repository,
        [Description("Issue title.")] string title,
        [Description("Issue description (Markdown).")] string body = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var issue = await CallAsync(HttpMethod.Post, $"repos/{Repo(repository)}/issues", new { title = ConnectorHttp.SingleLine(title), body }, cancellationToken).ConfigureAwait(false);
            return $"Issue #{issue?.GetProperty("number").GetInt32()} created: {issue?.Str("html_url")}";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> CommentAsync(
        [Description("Repository as owner/name.")] string repository,
        [Description("Issue or pull request number.")] int number,
        [Description("Comment text (Markdown).")] string body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var comment = await CallAsync(HttpMethod.Post, $"repos/{Repo(repository)}/issues/{number}/comments", new { body }, cancellationToken).ConfigureAwait(false);
            return $"Comment added: {comment?.Str("html_url")}";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private static string State(string state) => state.Trim().ToLowerInvariant() is "closed" or "all" ? state.Trim().ToLowerInvariant() : "open";
}
