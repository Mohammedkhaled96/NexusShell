using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Application.Connectors;
using NexusShell.Agents.Domain.Connectors;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.Providers;

namespace NexusShell.Agents.Infrastructure.Connectors.Google;

/// <summary>The user's Google account (one sign-in for Gmail and Google Calendar).</summary>
internal sealed class GoogleAccount(
    ISecretStore secrets,
    IHttpClientFactory httpClientFactory,
    IBrowserLauncher browser,
    TimeProvider time,
    ILogger<GoogleAccount> logger)
    : OAuthAccount(ProviderSettings, secrets, httpClientFactory, browser, time, logger)
{
    public const string ClientIdVariable = "GOOGLE_OAUTH_CLIENT_ID";
    public const string ClientSecretVariable = "GOOGLE_OAUTH_CLIENT_SECRET";
    public const string RefreshTokenVariable = "NEXUS_GOOGLE_REFRESH_TOKEN";

    public static OAuthProviderSettings ProviderSettings { get; } = new(
        "Google",
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        ClientIdVariable,
        ClientSecretVariable,
        RefreshTokenVariable,
        [
            "https://www.googleapis.com/auth/gmail.readonly",
            "https://www.googleapis.com/auth/gmail.compose",
            "https://www.googleapis.com/auth/calendar.events",
        ],
        "127.0.0.1",
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "true" },
        new Uri("https://oauth2.googleapis.com/revoke"));
}

/// <summary>Google: Gmail and Google Calendar.</summary>
internal sealed class GoogleConnector(
    GoogleAccount account,
    IHttpClientFactory httpClientFactory,
    ISecretStore secrets,
    IBrowserLauncher browser,
    ILogger<GoogleConnector> logger) : IConnector
{
    private const string ConsoleStart = "https://console.cloud.google.com/projectcreate";

    public static ConnectorDefinition Info { get; } = new()
    {
        Id = "google",
        Name = "Google (Gmail and Google Calendar)",
        Description = "Read and search your Gmail, write drafts, send and reply to e-mail; read your Google Calendar and add or remove events.",
        Auth = ConnectorAuthKind.OAuth,
        ToolCategories = [GmailToolProvider.CategoryName, GoogleCalendarToolProvider.CategoryName],
        TokenVariable = GoogleAccount.RefreshTokenVariable,
        Fields =
        [
            new(GoogleAccount.ClientIdVariable, "OAuth client ID", false, "From your Google Cloud project: Credentials, OAuth client ID of type Desktop app. Ends with .apps.googleusercontent.com."),
            new(GoogleAccount.ClientSecretVariable, "OAuth client secret", true, "Shown next to the client ID. Desktop apps cannot keep it secret, but it is still stored only in your environment."),
        ],
        SetupUrl = ConsoleStart,
        SupportsClientFile = true,
        SetupSteps =
        [
            "Google asks every app that reads Gmail to be registered once. It is free and takes about five minutes; after that, Sign in only opens your browser to choose your account.",
            "Step 1: press Create a project, name it NexusShell and press Create.",
            "Step 2: press Turn on Gmail, then Enable.",
            "Step 3: press Turn on Calendar, then Enable.",
            "Step 4: press Consent screen, then Get started: app name NexusShell, your e-mail, audience External. Then open Audience and add your own Gmail address under Test users.",
            "Step 5: press Create the client: application type Desktop app, name NexusShell, Create, then Download JSON.",
            "Step 6: come back and press Sign in. NexusShell finds the downloaded file in your Downloads folder by itself (or use Import client file), then your browser opens Google's page: choose your account and allow access. Your password never goes to NexusShell.",
            "Tip: while the app is in testing, Google asks you to sign in again every 7 days. To avoid that, open Audience and press Publish app (you will see a warning that Google has not verified the app; it is your own app, so choose Continue).",
        ],
        SetupLinks =
        [
            new("Step 1: Create a project", ConsoleStart),
            new("Step 2: Turn on Gmail", "https://console.cloud.google.com/apis/library/gmail.googleapis.com"),
            new("Step 3: Turn on Calendar", "https://console.cloud.google.com/apis/library/calendar-json.googleapis.com"),
            new("Step 4: Consent screen", "https://console.cloud.google.com/auth/overview"),
            new("Step 5: Create the client", "https://console.cloud.google.com/auth/clients/create"),
        ],
        Abilities =
        [
            new("Search and read your Gmail", false),
            new("Write Gmail drafts", true),
            new("Send e-mail and reply", true),
            new("Read your Google Calendar", false),
            new("Add or delete calendar events (attendees get invitations)", true),
        ],
    };

    public ConnectorDefinition Definition => Info;

    public ConnectorState CurrentState =>
        !account.HasClient ? ConnectorState.NeedsSetup : account.IsSignedIn ? ConnectorState.Connected : ConnectorState.NeedsSignIn;

    /// <summary>
    /// Sign in always does something useful: with the client registered it opens Google's account
    /// chooser; otherwise it imports a client file just downloaded, or opens the first setup page.
    /// </summary>
    public async Task<ConnectorStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!account.HasClient)
        {
            if (GoogleClientFile.FindInDownloads() is { } file)
                return await ImportClientFileAsync(file, cancellationToken).ConfigureAwait(false);

            browser.Open(new Uri(ConsoleStart));
            return new ConnectorStatus(ConnectorState.NeedsSetup,
                "Google needs a one-time, free registration before the first sign-in. Your browser opened Google Cloud on step 1. " +
                "Follow the step buttons here; when you have downloaded the client file (step 5), press Sign in again and NexusShell does the rest.");
        }

        await account.SignInAsync(cancellationToken).ConfigureAwait(false);
        return await TestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectorStatus> ImportClientFileAsync(string path, CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConnectorException("The client file could not be read: " + ex.Message);
        }

        var client = GoogleClientFile.Parse(json);
        await secrets.SetSecretAsync(new SecretReference(GoogleAccount.ClientIdVariable), client.ClientId, cancellationToken).ConfigureAwait(false);
        await secrets.SetSecretAsync(new SecretReference(GoogleAccount.ClientSecretVariable), client.ClientSecret, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Google client settings imported from {File}.", Path.GetFileName(path));

        await account.SignInAsync(cancellationToken).ConfigureAwait(false);
        var status = await TestAsync(cancellationToken).ConfigureAwait(false);
        return status with
        {
            Detail = status.Detail + $" The settings came from {Path.GetFileName(path)}; you can delete that file now (NexusShell keeps them in your user environment).",
        };
    }

    public async Task<ConnectorStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState != ConnectorState.Connected) return new ConnectorStatus(CurrentState, "Not signed in yet.");
        var profile = await ConnectorHttp.SendAsync(
            httpClientFactory.CreateClient(HttpClientNames.Connectors), "Gmail", HttpMethod.Get,
            "https://gmail.googleapis.com/gmail/v1/users/me/profile", account.GetAccessTokenAsync, null, cancellationToken).ConfigureAwait(false);
        string email = profile?.Str("emailAddress") ?? string.Empty;
        return new ConnectorStatus(ConnectorState.Connected, $"Connected to Google as {email}.", email);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => account.SignOutAsync(cancellationToken);
}

/// <summary>Category "gmail": search and read mail freely; drafts, sending and replies always ask.</summary>
internal sealed class GmailToolProvider(GoogleAccount account, IHttpClientFactory httpClientFactory) : IAgentToolProvider
{
    public const string CategoryName = "gmail";
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private const int MaxBodyChars = 12_000;

    public string Category => CategoryName;

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(SearchAsync, "gmail_search", "Search the user's Gmail (Gmail search syntax) and list messages with sender, subject, date and a preview."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(ReadAsync, "gmail_read", "Read one Gmail message in full by its id."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(DraftAsync, "gmail_create_draft", "Save an e-mail as a Gmail draft (not sent). The user approves it."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(SendAsync, "gmail_send", "Send an e-mail from the user's Gmail. The user approves the exact message first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(ReplyAsync, "gmail_reply", "Reply to a Gmail message in its thread. The user approves the exact reply first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
    }

    private HttpClient Http => httpClientFactory.CreateClient(HttpClientNames.Connectors);

    private Task<JsonElement?> CallAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        ConnectorHttp.SendAsync(Http, "Gmail", method, Api + path, account.GetAccessTokenAsync, body, cancellationToken);

    private async Task<string> SearchAsync(
        [Description("Gmail search, e.g. \"is:unread in:inbox\", \"from:boss@company.com newer_than:7d\", \"subject:invoice\".")] string query = "in:inbox",
        [Description("Maximum messages (1-25).")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await CallAsync(HttpMethod.Get, $"messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Clamp(maxResults, 1, 25)}", null, cancellationToken).ConfigureAwait(false);
            var ids = list is { } l ? l.Items("messages").Select(m => m.Str("id")).ToList() : [];
            if (ids.Count == 0) return $"No Gmail messages match \"{query}\".";

            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (string id in ids)
            {
                var message = await CallAsync(HttpMethod.Get, $"messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date", null, cancellationToken).ConfigureAwait(false);
                if (message is not { } m) continue;
                var headers = Headers(m);
                bool unread = m.Items("labelIds").Any(x => x.GetString() == "UNREAD");
                sb.Append("- [").Append(id).Append("] ").Append(headers.GetValueOrDefault("Date")).Append(" | ")
                  .Append(headers.GetValueOrDefault("From")).Append(" | ").Append(headers.GetValueOrDefault("Subject"))
                  .Append(unread ? " (unread)" : string.Empty).Append(" - ").AppendLine(System.Net.WebUtility.HtmlDecode(m.Str("snippet")));
            }
            return sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> ReadAsync([Description("Message id from gmail_search.")] string id, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await CallAsync(HttpMethod.Get, $"messages/{Uri.EscapeDataString(id)}?format=full", null, cancellationToken).ConfigureAwait(false);
            if (message is not { } m) return "The message is empty.";
            var payload = m.Obj("payload");
            var headers = Headers(m);
            var attachments = new List<string>();
            string body = ExtractBody(payload, attachments);

            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (string name in new[] { "From", "To", "Cc", "Date", "Subject" })
            {
                if (headers.TryGetValue(name, out string? value)) sb.Append(name).Append(": ").AppendLine(value);
            }
            if (attachments.Count > 0) sb.Append("Attachments: ").AppendLine(string.Join(", ", attachments));
            return sb.AppendLine().Append(ConnectorHttp.Clip(body, MaxBodyChars)).ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> DraftAsync(
        [Description("Recipients, comma separated.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Plain-text body.")] string body,
        [Description("Optional copy recipients, comma separated.")] string? cc = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string raw = BuildRaw(to, cc, subject, body, null);
            var draft = await CallAsync(HttpMethod.Post, "drafts", new { message = new { raw } }, cancellationToken).ConfigureAwait(false);
            return $"Draft saved in Gmail (id {draft?.Str("id")}). It was not sent.";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> SendAsync(
        [Description("Recipients, comma separated.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Plain-text body.")] string body,
        [Description("Optional copy recipients, comma separated.")] string? cc = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string raw = BuildRaw(to, cc, subject, body, null);
            var sent = await CallAsync(HttpMethod.Post, "messages/send", new { raw }, cancellationToken).ConfigureAwait(false);
            return $"E-mail sent to {to} (id {sent?.Str("id")}).";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> ReplyAsync(
        [Description("Id of the message to answer (from gmail_search).")] string id,
        [Description("Plain-text reply.")] string body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var original = await CallAsync(HttpMethod.Get, $"messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Reply-To&metadataHeaders=Subject&metadataHeaders=Message-ID&metadataHeaders=References", null, cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorException("The original message was not found.");
            var headers = Headers(original);
            string to = ExtractAddress(headers.GetValueOrDefault("Reply-To") ?? headers.GetValueOrDefault("From") ?? string.Empty);
            string subject = headers.GetValueOrDefault("Subject") ?? string.Empty;
            if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)) subject = "Re: " + subject;
            string? messageId = headers.GetValueOrDefault("Message-ID");
            string references = ((headers.GetValueOrDefault("References") ?? string.Empty) + " " + messageId).Trim();

            string raw = BuildRaw(to, null, subject, body, messageId is null ? null : (messageId, references));
            await CallAsync(HttpMethod.Post, "messages/send", new { raw, threadId = original.Str("threadId") }, cancellationToken).ConfigureAwait(false);
            return $"Reply sent to {to}.";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>RFC 2822 message, base64url; headers are single-line so nothing can be injected.</summary>
    internal static string BuildRaw(string to, string? cc, string subject, string body, (string MessageId, string References)? replyTo)
    {
        var recipients = ConnectorHttp.Addresses(to, "To");
        if (recipients.Count == 0) throw new ConnectorException("Give at least one recipient.");
        var copies = ConnectorHttp.Addresses(cc, "Cc");

        var sb = new StringBuilder();
        sb.Append("To: ").Append(string.Join(", ", recipients)).Append("\r\n");
        if (copies.Count > 0) sb.Append("Cc: ").Append(string.Join(", ", copies)).Append("\r\n");
        sb.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(ConnectorHttp.SingleLine(subject)))).Append("?=\r\n");
        if (replyTo is { } reply)
        {
            sb.Append("In-Reply-To: ").Append(ConnectorHttp.SingleLine(reply.MessageId)).Append("\r\n");
            sb.Append("References: ").Append(ConnectorHttp.SingleLine(reply.References)).Append("\r\n");
        }
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=\"UTF-8\"\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(body ?? string.Empty), Base64FormattingOptions.InsertLineBreaks));
        return OAuthAccount.Base64Url(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static Dictionary<string, string> Headers(JsonElement message) =>
        message.Obj("payload").Items("headers")
            .GroupBy(h => h.Str("name"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Str("value"), StringComparer.OrdinalIgnoreCase);

    private static string ExtractAddress(string header)
    {
        int open = header.LastIndexOf('<');
        int close = header.LastIndexOf('>');
        return open >= 0 && close > open ? header[(open + 1)..close].Trim() : header.Trim();
    }

    /// <summary>Plain text of a message: text/plain preferred, else HTML converted to text.</summary>
    internal static string ExtractBody(JsonElement part, List<string> attachments)
    {
        string? plain = null;
        string? html = null;
        Walk(part);
        return plain ?? (html is null ? "(no readable text)" : ConnectorHttp.HtmlToText(html));

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            string mime = node.Str("mimeType");
            string fileName = node.Str("filename");
            string data = node.Obj("body").Str("data");
            if (fileName.Length > 0) attachments.Add(fileName);
            else if (data.Length > 0 && mime == "text/plain") plain ??= ConnectorHttp.Base64UrlDecode(data);
            else if (data.Length > 0 && mime == "text/html") html ??= ConnectorHttp.Base64UrlDecode(data);
            foreach (var child in node.Items("parts")) Walk(child);
        }
    }
}

/// <summary>Category "google-calendar": read events freely; adding or deleting always asks.</summary>
internal sealed class GoogleCalendarToolProvider(GoogleAccount account, IHttpClientFactory httpClientFactory, TimeProvider time) : IAgentToolProvider
{
    public const string CategoryName = "google-calendar";
    private const string Api = "https://www.googleapis.com/calendar/v3/calendars/primary/events";

    public string Category => CategoryName;

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(ListAsync, "google_calendar_list_events", "List events in the user's Google Calendar for a period."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(CreateAsync, "google_calendar_create_event", "Add an event to the user's Google Calendar (attendees get invitations). The user approves it first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(DeleteAsync, "google_calendar_delete_event", "Delete an event from the user's Google Calendar by id. The user approves it first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
    }

    private TimeZoneInfo Zone => time.LocalTimeZone;

    private Task<JsonElement?> CallAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken) =>
        ConnectorHttp.SendAsync(httpClientFactory.CreateClient(HttpClientNames.Connectors), "Google Calendar", method, url, account.GetAccessTokenAsync, body, cancellationToken);

    private async Task<string> ListAsync(
        [Description("First day: today, tomorrow, or a date such as 2026-11-24.")] string from = "today",
        [Description("Number of days to list (1-62).")] int days = 7,
        [Description("Maximum events (1-50).")] int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var start = ConnectorTime.DayOrTime(from, time.GetUtcNow(), Zone);
            var end = start.AddDays(Math.Clamp(days, 1, 62));
            var result = await CallAsync(HttpMethod.Get,
                $"{Api}?singleEvents=true&orderBy=startTime&maxResults={Math.Clamp(maxResults, 1, 50)}&timeMin={Uri.EscapeDataString(ConnectorTime.Rfc3339(start))}&timeMax={Uri.EscapeDataString(ConnectorTime.Rfc3339(end))}",
                null, cancellationToken).ConfigureAwait(false);
            var events = result is { } r ? r.Items("items").ToList() : [];
            if (events.Count == 0) return $"No events from {ConnectorTime.Describe(start, end, Zone)}.";

            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var e in events)
            {
                sb.Append("- [").Append(e.Str("id")).Append("] ").Append(When(e)).Append(" | ").Append(e.Str("summary").Length > 0 ? e.Str("summary") : "(no title)");
                if (e.Str("location").Length > 0) sb.Append(" | at ").Append(e.Str("location"));
                int attendees = e.Items("attendees").Count();
                if (attendees > 0) sb.Append(" | ").Append(attendees).Append(" attendees");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is ConnectorException or ArgumentException)
        {
            return ex.Message;
        }
    }

    private string When(JsonElement e)
    {
        var start = e.Obj("start");
        var end = e.Obj("end");
        if (start.Str("date").Length > 0) return start.Str("date") + " (all day)";
        return DateTimeOffset.TryParse(start.Str("dateTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var s)
            ? ConnectorTime.Describe(s, DateTimeOffset.TryParse(end.Str("dateTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var en) ? en : null, Zone)
            : start.Str("dateTime");
    }

    private async Task<string> CreateAsync(
        [Description("Event title.")] string title,
        [Description("Start, in the user's local time: \"2026-11-24 14:00\", \"tomorrow 09:30\" or \"15:00\".")] string start,
        [Description("Length in minutes.")] int durationMinutes = 60,
        [Description("Optional attendee e-mails, comma separated (they get invitations).")] string? attendees = null,
        [Description("Optional description.")] string? description = null,
        [Description("Optional place.")] string? location = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var begin = ConnectorTime.Start(start, time.GetUtcNow(), Zone);
            var finish = begin.AddMinutes(Math.Clamp(durationMinutes, 5, 24 * 60 * 14));
            var guests = ConnectorHttp.Addresses(attendees, "attendees");
            var created = await CallAsync(HttpMethod.Post, $"{Api}?sendUpdates={(guests.Count > 0 ? "all" : "none")}", new
            {
                summary = ConnectorHttp.SingleLine(title),
                description,
                location,
                start = new { dateTime = ConnectorTime.Rfc3339(begin) },
                end = new { dateTime = ConnectorTime.Rfc3339(finish) },
                attendees = guests.Select(g => new { email = g }).ToList(),
            }, cancellationToken).ConfigureAwait(false);
            return $"Event \"{title}\" added for {ConnectorTime.Describe(begin, finish, Zone)} (id {created?.Str("id")})" +
                   (guests.Count > 0 ? $"; invitations sent to {string.Join(", ", guests)}." : ".");
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> DeleteAsync([Description("Event id from google_calendar_list_events.")] string id, CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync(HttpMethod.Delete, $"{Api}/{Uri.EscapeDataString(id)}", null, cancellationToken).ConfigureAwait(false);
            return $"Event {id} deleted.";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }
}
