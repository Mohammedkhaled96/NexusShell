using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Application.Connectors;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Domain.Connectors;
using NexusShell.Agents.Infrastructure.Connectors;
using NexusShell.Agents.Infrastructure.Connectors.GitHub;
using NexusShell.Agents.Infrastructure.Connectors.Google;
using NexusShell.Agents.Infrastructure.Connectors.Microsoft;
using NexusShell.Agents.Infrastructure.Mcp;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>Connectors: browser sign-in (OAuth + PKCE), Gmail / Calendar / Outlook / GitHub tools, MCP templates.</summary>
[Collection("Environment variables")]
public sealed class ConnectorsTests : IDisposable
{
    private static readonly string[] Variables =
    [
        GoogleAccount.ClientIdVariable, GoogleAccount.ClientSecretVariable, GoogleAccount.RefreshTokenVariable,
        MicrosoftAccount.ClientIdVariable, MicrosoftAccount.RefreshTokenVariable, "NOTION_TOKEN",
    ];

    private readonly AgentTestHost _host = new();
    private readonly FakeWeb _web = new();
    private readonly FakeBrowser _browser = new();

    private readonly Func<string> _realDownloads = GoogleClientFile.DownloadsFolder;

    public ConnectorsTests()
    {
        foreach (string variable in Variables) Environment.SetEnvironmentVariable(variable, null);
        // Never read the real Downloads folder in tests.
        Directory.CreateDirectory(Downloads);
        GoogleClientFile.DownloadsFolder = () => Downloads;
    }

    private string Downloads => Path.Combine(_host.Root, "Downloads");

    public void Dispose()
    {
        foreach (string variable in Variables) Environment.SetEnvironmentVariable(variable, null);
        GoogleClientFile.DownloadsFolder = _realDownloads;
        _host.Dispose();
    }

    private const string DesktopClientFile =
        """{"installed":{"client_id":"9876-abc.apps.googleusercontent.com","project_id":"nexusshell","auth_uri":"https://accounts.google.com/o/oauth2/auth","token_uri":"https://oauth2.googleapis.com/token","client_secret":"desktop-secret-value","redirect_uris":["http://localhost"]}}""";

    // ---------- One-click Google sign-in ----------

    [Fact]
    public void Google_client_files_are_read_and_wrong_kinds_explained()
    {
        var client = GoogleClientFile.Parse(DesktopClientFile);
        Assert.Equal("9876-abc.apps.googleusercontent.com", client.ClientId);
        Assert.Equal("desktop-secret-value", client.ClientSecret);

        Assert.Contains("Desktop app", Assert.Throws<ConnectorException>(() => GoogleClientFile.Parse("""{"web":{"client_id":"x.apps.googleusercontent.com","client_secret":"y"}}""")).Message, StringComparison.Ordinal);
        Assert.Throws<ConnectorException>(() => GoogleClientFile.Parse("not json"));
        Assert.Throws<ConnectorException>(() => GoogleClientFile.Parse("""{"installed":{"client_id":"wrong"}}"""));
    }

    [Fact]
    public async Task Sign_in_without_a_registered_client_opens_the_google_setup_page()
    {
        await using var services = Build();

        var status = await services.GetRequiredService<IConnectorService>().ConnectAsync("google");

        Assert.Equal(ConnectorState.NeedsSetup, status.State);
        Assert.Contains("one-time", status.Detail, StringComparison.Ordinal);
        Assert.StartsWith("https://console.cloud.google.com/", Assert.Single(_browser.Opened).AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_picks_up_a_downloaded_client_file_and_goes_straight_to_the_account_chooser()
    {
        File.WriteAllText(Path.Combine(Downloads, "client_secret_9876-abc.apps.googleusercontent.com.json"), DesktopClientFile);
        _web.Respond("oauth2.googleapis.com/token", """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}""");
        _web.Respond("gmail/v1/users/me/profile", """{"emailAddress":"me@gmail.com"}""");
        await using var services = Build();

        var status = await services.GetRequiredService<IConnectorService>().ConnectAsync("google");

        Assert.Equal(ConnectorState.Connected, status.State);
        Assert.Equal("me@gmail.com", status.Account);
        Assert.Contains("you can delete that file", status.Detail, StringComparison.Ordinal);
        Assert.Equal("9876-abc.apps.googleusercontent.com", Environment.GetEnvironmentVariable(GoogleAccount.ClientIdVariable));
        Assert.Equal("refresh-1", Environment.GetEnvironmentVariable(GoogleAccount.RefreshTokenVariable));
        Assert.StartsWith("https://accounts.google.com/", Assert.Single(_browser.Opened).AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(_web.Requests, r => r.Body.Contains("client_secret=desktop-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_chosen_client_file_is_imported_and_signed_in()
    {
        string file = Path.Combine(_host.Root, "my-client.json");
        File.WriteAllText(file, DesktopClientFile);
        _web.Respond("oauth2.googleapis.com/token", """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}""");
        _web.Respond("gmail/v1/users/me/profile", """{"emailAddress":"me@gmail.com"}""");
        await using var services = Build();

        var status = await services.GetRequiredService<IConnectorService>().ImportClientFileAsync("google", file);

        Assert.Equal(ConnectorState.Connected, status.State);
        await Assert.ThrowsAsync<NotSupportedException>(() => services.GetRequiredService<IConnectorService>().ImportClientFileAsync("github", file));
    }

    [Fact]
    public async Task Connect_without_a_github_token_opens_the_token_page()
    {
        string? previous = Environment.GetEnvironmentVariable(GitHubConnector.TokenVariable, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(GitHubConnector.TokenVariable, null);
        try
        {
            await using var services = Build();
            if (services.GetRequiredService<IConnectorService>().List().Single(c => c.Definition.Id == "github").State == ConnectorState.Connected)
                return; // a real GITHUB_TOKEN exists in the user environment: nothing to check here

            var status = await services.GetRequiredService<IConnectorService>().ConnectAsync("github");

            Assert.Equal(ConnectorState.NeedsSetup, status.State);
            Assert.Equal("https://github.com/settings/personal-access-tokens/new", Assert.Single(_browser.Opened).AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GitHubConnector.TokenVariable, previous);
        }
    }

    private ServiceProvider Build() => _host.Build(s =>
    {
        s.AddSingleton<IBrowserLauncher>(_browser);
        s.AddHttpClient(HttpClientNames.Connectors).ConfigurePrimaryHttpMessageHandler(() => _web);
    });

    private static void GoogleClient()
    {
        Environment.SetEnvironmentVariable(GoogleAccount.ClientIdVariable, "client-123.apps.googleusercontent.com");
        Environment.SetEnvironmentVariable(GoogleAccount.ClientSecretVariable, "desktop-client-secret");
    }

    private T Tool<T>(ServiceProvider services) where T : IAgentToolProvider =>
        services.GetServices<IAgentToolProvider>().OfType<T>().Single();

    private static async Task<string> Call(IAgentToolProvider provider, string name, Dictionary<string, object?> args)
    {
        var function = provider.GetTools().Single(t => t.Name == name).Function;
        return (await function.InvokeAsync(new AIFunctionArguments(args)))?.ToString() ?? string.Empty;
    }

    // ---------- Sign-in ----------

    [Fact]
    public async Task Google_sign_in_uses_the_browser_with_pkce_and_keeps_only_the_refresh_token()
    {
        GoogleClient();
        _web.Respond("oauth2.googleapis.com/token", """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}""");
        _web.Respond("gmail/v1/users/me/profile", """{"emailAddress":"user@example.com"}""");
        await using var services = Build();
        var connectors = services.GetRequiredService<IConnectorService>();
        Assert.Equal(ConnectorState.NeedsSignIn, connectors.List().Single(c => c.Definition.Id == "google").State);

        var status = await connectors.ConnectAsync("google");

        Assert.Equal(ConnectorState.Connected, status.State);
        Assert.Equal("user@example.com", status.Account);
        var authorize = _browser.Opened.Single();
        var query = LoopbackListener.ParseQuery(authorize.PathAndQuery);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Contains("gmail.readonly", query["scope"], StringComparison.Ordinal);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"], StringComparison.Ordinal);

        var token = _web.Requests.Single(r => r.Url.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal));
        Assert.Contains("grant_type=authorization_code", token.Body, StringComparison.Ordinal);
        Assert.Contains("code=code-from-google", token.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", token.Body, StringComparison.Ordinal);
        Assert.Equal("refresh-1", Environment.GetEnvironmentVariable(GoogleAccount.RefreshTokenVariable));
        Assert.Equal(ConnectorState.Connected, connectors.List().Single(c => c.Definition.Id == "google").State);
    }

    [Fact]
    public async Task A_sign_in_answer_with_the_wrong_state_is_rejected()
    {
        GoogleClient();
        _browser.TamperState = true;
        await using var services = Build();

        var error = await Assert.ThrowsAsync<ConnectorException>(() => services.GetRequiredService<IConnectorService>().ConnectAsync("google"));

        Assert.Contains("did not match", error.Message, StringComparison.Ordinal);
        Assert.Null(Environment.GetEnvironmentVariable(GoogleAccount.RefreshTokenVariable));
    }

    [Fact]
    public async Task A_revoked_sign_in_is_forgotten_and_the_user_is_told_to_sign_in_again()
    {
        GoogleClient();
        Environment.SetEnvironmentVariable(GoogleAccount.RefreshTokenVariable, "old-refresh");
        _web.Respond("oauth2.googleapis.com/token", """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest);
        await using var services = Build();

        string result = await Call(Tool<GmailToolProvider>(services), "gmail_search", new() { ["query"] = "is:unread" });

        Assert.Contains("sign in again", result, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Environment.GetEnvironmentVariable(GoogleAccount.RefreshTokenVariable));
    }

    // ---------- Gmail ----------

    [Fact]
    public async Task Gmail_search_lists_messages_as_external_data()
    {
        GoogleClient();
        Environment.SetEnvironmentVariable(GoogleAccount.RefreshTokenVariable, "refresh-1");
        _web.Respond("oauth2.googleapis.com/token", """{"access_token":"access-2","expires_in":3600}""");
        _web.Respond("messages?q=", """{"messages":[{"id":"m1"}]}""");
        _web.Respond("messages/m1?format=metadata", """{"id":"m1","snippet":"Please pay by Friday &amp; thanks","labelIds":["UNREAD"],"payload":{"headers":[{"name":"From","value":"Billing <bill@shop.com>"},{"name":"Subject","value":"Invoice 42"},{"name":"Date","value":"Mon, 23 Nov 2026"}]}}""");
        await using var services = Build();

        string result = await Call(Tool<GmailToolProvider>(services), "gmail_search", new() { ["query"] = "subject:invoice" });

        Assert.StartsWith(ConnectorHttp.ExternalContentNote, result, StringComparison.Ordinal);
        Assert.Contains("[m1]", result, StringComparison.Ordinal);
        Assert.Contains("Invoice 42 (unread)", result, StringComparison.Ordinal);
        Assert.Contains("Please pay by Friday & thanks", result, StringComparison.Ordinal);
        Assert.All(_web.Requests.Where(r => r.Url.Contains("gmail", StringComparison.Ordinal)), r => Assert.Equal("Bearer access-2", r.Authorization));
    }

    [Fact]
    public void Mail_headers_cannot_be_injected_and_subjects_stay_on_one_line()
    {
        Assert.Throws<ConnectorException>(() => GmailToolProvider.BuildRaw("a@b.com\r\nBcc: spy@evil.com", null, "Hi", "body", null));
        Assert.Throws<ConnectorException>(() => GmailToolProvider.BuildRaw("not an address", null, "Hi", "body", null));

        string raw = GmailToolProvider.BuildRaw("a@b.com, c@d.org", "e@f.net", "Hello\r\nBcc: spy@evil.com", "Body text", null);
        string message = ConnectorHttp.Base64UrlDecode(raw);
        Assert.Contains("To: a@b.com, c@d.org\r\n", message, StringComparison.Ordinal);
        Assert.Contains("Cc: e@f.net\r\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nBcc:", message, StringComparison.Ordinal);
        string encodedSubject = message.Split("\r\n").Single(l => l.StartsWith("Subject:", StringComparison.Ordinal));
        Assert.DoesNotContain('\n', Encoding.UTF8.GetString(Convert.FromBase64String(encodedSubject["Subject: =?UTF-8?B?".Length..^2])));
    }

    [Fact]
    public void Mail_bodies_prefer_plain_text_and_fall_back_to_cleaned_html()
    {
        static JsonElement Part(string json) => JsonDocument.Parse(json).RootElement.Clone();
        string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var attachments = new List<string>();
        var plain = Part($$$"""{"mimeType":"multipart/alternative","parts":[{"mimeType":"text/html","body":{"data":"{{{Encode("<b>Hi</b>")}}}"}},{"mimeType":"text/plain","body":{"data":"{{{Encode("Hi there")}}}"}},{"mimeType":"application/pdf","filename":"bill.pdf","body":{}}]}""");
        Assert.Equal("Hi there", GmailToolProvider.ExtractBody(plain, attachments));
        Assert.Equal(["bill.pdf"], attachments);

        var html = Part($$$"""{"mimeType":"text/html","body":{"data":"{{{Encode("<p>Hello <b>World</b></p><script>alert(1)</script><p>Bye &amp; thanks</p>")}}}"}}""");
        string text = GmailToolProvider.ExtractBody(html, []);
        Assert.Contains("Hello World", text, StringComparison.Ordinal);
        Assert.Contains("Bye & thanks", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_is_free_but_sending_and_changing_always_ask_first()
    {
        await using var services = Build();
        var tools = services.GetServices<IAgentToolProvider>()
            .Where(p => p.Category is "gmail" or "google-calendar" or "outlook-mail" or "outlook-calendar" or "github")
            .SelectMany(p => p.GetTools())
            .ToList();

        Assert.All(tools.Where(t => t.Name.Contains("send", StringComparison.Ordinal) || t.Name.Contains("reply", StringComparison.Ordinal) || t.Name.Contains("create", StringComparison.Ordinal) || t.Name.Contains("delete", StringComparison.Ordinal) || t.Name.Contains("comment", StringComparison.Ordinal)),
            t => Assert.True(t.ForceApproval, t.Name + " must ask first"));
        Assert.All(tools.Where(t => t.Name.Contains("search", StringComparison.Ordinal) || t.Name.Contains("read", StringComparison.Ordinal) || t.Name.Contains("list", StringComparison.Ordinal)),
            t => Assert.False(t.ForceApproval, t.Name + " is read-only"));
        Assert.Equal(5 + 3 + 5 + 3 + 8, tools.Count);
    }

    // ---------- Outlook and GitHub ----------

    [Fact]
    public async Task Outlook_sends_mail_through_microsoft_graph()
    {
        Environment.SetEnvironmentVariable(MicrosoftAccount.ClientIdVariable, "11111111-2222-3333-4444-555555555555");
        Environment.SetEnvironmentVariable(MicrosoftAccount.RefreshTokenVariable, "ms-refresh");
        _web.Respond("login.microsoftonline.com/common/oauth2/v2.0/token", """{"access_token":"ms-access","refresh_token":"ms-refresh-2","expires_in":3600}""");
        _web.Respond("graph.microsoft.com/v1.0/me/sendMail", string.Empty, HttpStatusCode.Accepted);
        await using var services = Build();

        string result = await Call(Tool<OutlookMailToolProvider>(services), "outlook_send_mail",
            new() { ["to"] = "friend@example.com", ["subject"] = "Lunch", ["body"] = "Tomorrow at 1?" });

        Assert.Equal("E-mail sent to friend@example.com.", result);
        var send = _web.Requests.Single(r => r.Url.EndsWith("/me/sendMail", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(send.Body);
        Assert.Equal("friend@example.com", json.RootElement.GetProperty("message").GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("ms-refresh-2", Environment.GetEnvironmentVariable(MicrosoftAccount.RefreshTokenVariable)); // rotated token kept
        Assert.DoesNotContain(_web.Requests, r => r.Body.Contains("client_secret", StringComparison.Ordinal)); // public client
    }

    [Fact]
    public async Task GitHub_lists_issues_without_pull_requests_and_validates_repository_names()
    {
        string? previous = Environment.GetEnvironmentVariable(GitHubConnector.TokenVariable);
        Environment.SetEnvironmentVariable(GitHubConnector.TokenVariable, "test-token-value");
        try
        {
            _web.Respond("repos/octo/app/issues?state=open", """[{"number":1,"title":"Crash on start","state":"open","user":{"login":"ana"}},{"number":2,"title":"A PR","state":"open","user":{"login":"bo"},"pull_request":{}}]""");
            await using var services = Build();
            var github = Tool<GitHubToolProvider>(services);

            string issues = await Call(github, "github_list_issues", new() { ["repository"] = "octo/app" });
            string bad = await Call(github, "github_list_issues", new() { ["repository"] = "../../etc" });

            Assert.Contains("#1 Crash on start (by ana, open)", issues, StringComparison.Ordinal);
            Assert.DoesNotContain("A PR", issues, StringComparison.Ordinal);
            Assert.Contains("is not a repository", bad, StringComparison.Ordinal);
            Assert.Equal("Bearer test-token-value", _web.Requests.Single().Authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GitHubConnector.TokenVariable, previous);
        }
    }

    // ---------- Connectors in the agent ----------

    [Fact]
    public async Task Connected_apps_give_every_expert_their_tools_and_disconnected_ones_do_not()
    {
        _host.Expert("alpha", "Alpha", "apples");
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = Build();
        var agent = services.GetRequiredService<INexusAgentService>();

        await (await agent.StartConversationAsync()).SendAsync("apples?");
        Assert.DoesNotContain(_host.Chat.Calls.Last().Options!.Tools!, t => t.Name.StartsWith("gmail_", StringComparison.Ordinal));

        GoogleClient();
        Environment.SetEnvironmentVariable(GoogleAccount.RefreshTokenVariable, "refresh-1");
        await (await agent.StartConversationAsync()).SendAsync("apples?");
        var offered = _host.Chat.Calls.Last().Options!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("gmail_search", offered);
        Assert.Contains("google_calendar_create_event", offered);
        Assert.DoesNotContain("outlook_send_mail", offered);
    }

    [Fact]
    public async Task Settings_values_are_checked_before_they_are_saved()
    {
        await using var services = Build();
        var connectors = services.GetRequiredService<IConnectorService>();

        await Assert.ThrowsAsync<ArgumentException>(() => connectors.SaveFieldAsync("google", GoogleAccount.ClientIdVariable, "sk-proj-" + new string('k', 30)));
        await Assert.ThrowsAsync<ArgumentException>(() => connectors.SaveFieldAsync("google", "PATH", "x"));
        await connectors.SaveFieldAsync("google", GoogleAccount.ClientIdVariable, "client-9.apps.googleusercontent.com");

        Assert.Equal("client-9.apps.googleusercontent.com", Environment.GetEnvironmentVariable(GoogleAccount.ClientIdVariable));
        Assert.Equal(ConnectorState.NeedsSetup, connectors.List().Single(c => c.Definition.Id == "google").State); // secret still missing
    }

    // ---------- MCP templates ----------

    [Fact]
    public async Task Mcp_templates_are_added_with_variable_references_and_shared_with_every_expert()
    {
        await using var services = Build();
        var connectors = services.GetRequiredService<IConnectorService>();

        await connectors.AddMcpAsync("notion");

        string json = File.ReadAllText(Path.Combine(_host.User, "mcp.json"));
        Assert.Contains("\"NOTION_TOKEN\": \"${env:NOTION_TOKEN}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"allExperts\": true", json, StringComparison.Ordinal);
        var server = (await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync()).McpServers.Single(s => s.Name == "notion");
        Assert.True(server.ShareWithAllExperts);
        Assert.Equal("npx", server.Command);
        var info = (await connectors.ListMcpAsync()).Single(p => p.Preset.Id == "notion");
        Assert.True(info.Added);
        Assert.False(info.Ready); // the token is not set yet

        await connectors.RemoveMcpAsync("notion");
        Assert.DoesNotContain((await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync()).McpServers, s => s.Name == "notion");
    }

    [Fact]
    public void Windows_command_launchers_run_through_cmd()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("cmd.exe", McpToolProvider.LaunchCommand("npx"));
        Assert.Equal(["/c", "npx", "-y", "pkg"], McpToolProvider.LaunchArguments("npx", ["-y", "pkg"]));
        Assert.Equal("uvx.exe", McpToolProvider.LaunchCommand("uvx.exe"));
    }

    // ---------- Fakes ----------

    /// <summary>A fake browser: "signs in" by calling the loopback redirect address with a code.</summary>
    private sealed class FakeBrowser : IBrowserLauncher
    {
        public ConcurrentQueue<Uri> Opened { get; } = new();

        public bool TamperState { get; set; }

        public void Open(Uri address)
        {
            Opened.Enqueue(address);
            var query = LoopbackListener.ParseQuery(address.PathAndQuery);
            if (!query.ContainsKey("redirect_uri")) return; // a setup page, not a sign-in
            string state = TamperState ? "forged-state" : query["state"];
            string callback = query["redirect_uri"] + "?code=code-from-google&state=" + Uri.EscapeDataString(state);
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                try
                {
                    await http.GetStringAsync(callback);
                }
                catch (HttpRequestException)
                {
                }
            });
        }
    }

    /// <summary>Answers connector web calls by URL fragment and records every request.</summary>
    private sealed class FakeWeb : HttpMessageHandler
    {
        private readonly List<(string Fragment, string Body, HttpStatusCode Status)> _routes = [];

        public ConcurrentQueue<(string Url, string Body, string? Authorization)> RequestLog { get; } = new();

        public IReadOnlyList<(string Url, string Body, string? Authorization)> Requests => RequestLog.ToList();

        public void Respond(string fragment, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes.Add((fragment, body, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestLog.Enqueue((Uri.UnescapeDataString(url), Uri.UnescapeDataString(body.Replace('+', ' ')), request.Headers.Authorization?.ToString()));
            var route = _routes.FirstOrDefault(r => Uri.UnescapeDataString(url).Contains(r.Fragment, StringComparison.Ordinal));
            return route.Fragment is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
        }
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
