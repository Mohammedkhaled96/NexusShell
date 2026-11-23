using System.ComponentModel;
using System.Globalization;
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

namespace NexusShell.Agents.Infrastructure.Connectors.Microsoft;

/// <summary>The user's Microsoft account (Outlook.com, Hotmail or Microsoft 365) through Microsoft Graph.</summary>
internal sealed class MicrosoftAccount(
    ISecretStore secrets,
    IHttpClientFactory httpClientFactory,
    IBrowserLauncher browser,
    TimeProvider time,
    ILogger<MicrosoftAccount> logger)
    : OAuthAccount(Create(secrets), secrets, httpClientFactory, browser, time, logger)
{
    public const string ClientIdVariable = "MICROSOFT_OAUTH_CLIENT_ID";
    public const string TenantVariable = "MICROSOFT_OAUTH_TENANT";
    public const string RefreshTokenVariable = "NEXUS_MICROSOFT_REFRESH_TOKEN";

    internal static readonly IReadOnlyList<string> Scopes =
        ["offline_access", "User.Read", "Mail.ReadWrite", "Mail.Send", "Calendars.ReadWrite"];

    /// <summary>"common" (personal and work accounts) unless the user set a tenant.</summary>
    private static OAuthProviderSettings Create(ISecretStore secrets)
    {
        string tenant = secrets.GetSecret(new SecretReference(TenantVariable)) is { Length: > 0 } t ? Uri.EscapeDataString(t.Trim()) : "common";
        return new OAuthProviderSettings(
            "Microsoft",
            new Uri($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize"),
            new Uri($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token"),
            ClientIdVariable,
            null, // public client: no secret, PKCE instead
            RefreshTokenVariable,
            Scopes,
            "localhost",
            new Dictionary<string, string> { ["prompt"] = "select_account" });
    }
}

/// <summary>Microsoft: Outlook mail and calendar (personal or work account).</summary>
internal sealed class MicrosoftConnector(MicrosoftAccount account, IHttpClientFactory httpClientFactory, IBrowserLauncher browser) : IConnector
{
    private const string RegisterPage = "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade";

    public static ConnectorDefinition Info { get; } = new()
    {
        Id = "microsoft",
        Name = "Microsoft (Outlook mail and calendar)",
        Description = "Read and search Outlook mail, write drafts, send and reply; read your Outlook calendar and add or remove events. Works with Outlook.com, Hotmail and Microsoft 365 accounts.",
        Auth = ConnectorAuthKind.OAuth,
        ToolCategories = [OutlookMailToolProvider.CategoryName, OutlookCalendarToolProvider.CategoryName],
        TokenVariable = MicrosoftAccount.RefreshTokenVariable,
        Fields =
        [
            new(MicrosoftAccount.ClientIdVariable, "Application (client) ID", false, "From your app registration in Microsoft Entra. A value like 1a2b3c4d-...."),
            new(MicrosoftAccount.TenantVariable, "Tenant (optional)", false, "Leave empty to allow personal and work accounts. A work account may need your organization's tenant id or domain.", Optional: true),
        ],
        SetupUrl = RegisterPage,
        SetupLinks =
        [
            new("Step 1: Register the app", RegisterPage),
            new("Step 2: My app registrations", "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"),
        ],
        SetupSteps =
        [
            "Open the setup page (Microsoft Entra, App registrations) and choose New registration.",
            "Name it NexusShell. For supported account types choose accounts in any organizational directory and personal Microsoft accounts.",
            "Redirect URI: choose the platform Public client/native (mobile and desktop) and type http://localhost",
            "Register, then open API permissions and add Microsoft Graph delegated permissions: User.Read, Mail.ReadWrite, Mail.Send, Calendars.ReadWrite and offline_access.",
            "Copy the Application (client) ID, paste it here and press Save. No client secret is needed.",
            "Press Sign in. Your browser opens Microsoft's own page: sign in and accept. Your password never goes to NexusShell.",
        ],
        Abilities =
        [
            new("Search and read your Outlook mail", false),
            new("Write Outlook drafts", true),
            new("Send e-mail and reply", true),
            new("Read your Outlook calendar", false),
            new("Add or delete calendar events (attendees get invitations)", true),
        ],
    };

    public ConnectorDefinition Definition => Info;

    public ConnectorState CurrentState =>
        !account.HasClient ? ConnectorState.NeedsSetup : account.IsSignedIn ? ConnectorState.Connected : ConnectorState.NeedsSignIn;

    public async Task<ConnectorStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!account.HasClient)
        {
            browser.Open(new Uri(RegisterPage));
            return new ConnectorStatus(ConnectorState.NeedsSetup,
                "Microsoft needs a one-time, free app registration before the first sign-in. Your browser opened the registration page. " +
                "Follow the steps here, paste the Application (client) ID, press Save, then press Sign in again.");
        }
        await account.SignInAsync(cancellationToken).ConfigureAwait(false);
        return await TestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectorStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState != ConnectorState.Connected) return new ConnectorStatus(CurrentState, "Not signed in yet.");
        var me = await ConnectorHttp.SendAsync(httpClientFactory.CreateClient(HttpClientNames.Connectors), "Outlook", HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/me?$select=displayName,mail,userPrincipalName", account.GetAccessTokenAsync, null, cancellationToken).ConfigureAwait(false);
        string who = me?.Str("mail") is { Length: > 0 } mail ? mail : me?.Str("userPrincipalName") ?? string.Empty;
        return new ConnectorStatus(ConnectorState.Connected, $"Connected to Microsoft as {who}.", who);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => account.SignOutAsync(cancellationToken);
}

/// <summary>Shared Microsoft Graph calls.</summary>
internal abstract class GraphTools(MicrosoftAccount account, IHttpClientFactory httpClientFactory)
{
    protected const string Graph = "https://graph.microsoft.com/v1.0/me/";

    protected Task<JsonElement?> CallAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null) =>
        ConnectorHttp.SendAsync(httpClientFactory.CreateClient(HttpClientNames.Connectors), "Outlook", method, Graph + path, account.GetAccessTokenAsync, body, cancellationToken, headers);

    protected static object Recipients(IReadOnlyList<string> addresses) =>
        addresses.Select(a => new { emailAddress = new { address = a } }).ToList();
}

/// <summary>Category "outlook-mail": search and read freely; drafts, sending and replies always ask.</summary>
internal sealed class OutlookMailToolProvider(MicrosoftAccount account, IHttpClientFactory httpClientFactory)
    : GraphTools(account, httpClientFactory), IAgentToolProvider
{
    public const string CategoryName = "outlook-mail";
    private const string Fields = "id,subject,from,receivedDateTime,bodyPreview,isRead";

    public string Category => CategoryName;

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(SearchAsync, "outlook_search_mail", "Search or list the user's Outlook mail with sender, subject, date and a preview."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(ReadAsync, "outlook_read_mail", "Read one Outlook message in full by its id."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(DraftAsync, "outlook_create_draft", "Save an e-mail as an Outlook draft (not sent). The user approves it."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(SendAsync, "outlook_send_mail", "Send an e-mail from the user's Outlook. The user approves the exact message first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(ReplyAsync, "outlook_reply", "Reply to an Outlook message. The user approves the exact reply first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
    }

    private async Task<string> SearchAsync(
        [Description("Words to search for (sender, subject, text). Empty lists the newest inbox messages.")] string query = "",
        [Description("Only unread messages (ignored when searching).")] bool unreadOnly = false,
        [Description("Maximum messages (1-25).")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            int top = Math.Clamp(maxResults, 1, 25);
            string path = string.IsNullOrWhiteSpace(query)
                ? $"mailFolders/inbox/messages?$top={top}&$select={Fields}&$orderby=receivedDateTime desc" + (unreadOnly ? "&$filter=isRead eq false" : string.Empty)
                : $"messages?$top={top}&$select={Fields}&$search={Uri.EscapeDataString("\"" + query.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"")}";
            var result = await CallAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            var messages = result is { } r ? r.Items("value").ToList() : [];
            if (messages.Count == 0) return string.IsNullOrWhiteSpace(query) ? "The inbox has no matching messages." : $"No Outlook messages match \"{query}\".";

            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var m in messages)
            {
                var from = m.Obj("from").Obj("emailAddress");
                bool read = m.TryGetProperty("isRead", out var isRead) && isRead.ValueKind == JsonValueKind.True;
                sb.Append("- [").Append(m.Str("id")).Append("] ").Append(m.Str("receivedDateTime")).Append(" | ")
                  .Append(from.Str("name")).Append(" <").Append(from.Str("address")).Append("> | ").Append(m.Str("subject"))
                  .Append(read ? string.Empty : " (unread)").Append(" - ").AppendLine(m.Str("bodyPreview"));
            }
            return sb.ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> ReadAsync([Description("Message id from outlook_search_mail.")] string id, CancellationToken cancellationToken = default)
    {
        try
        {
            var m = await CallAsync(HttpMethod.Get, $"messages/{Uri.EscapeDataString(id)}?$select=subject,from,toRecipients,ccRecipients,receivedDateTime,body,hasAttachments", null, cancellationToken,
                new Dictionary<string, string> { ["Prefer"] = "outlook.body-content-type=\"text\"" }).ConfigureAwait(false);
            if (m is not { } message) return "The message is empty.";
            string body = message.Obj("body").Str("content");
            if (message.Obj("body").Str("contentType").Equals("html", StringComparison.OrdinalIgnoreCase)) body = ConnectorHttp.HtmlToText(body);

            var from = message.Obj("from").Obj("emailAddress");
            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine()
                .Append("From: ").Append(from.Str("name")).Append(" <").Append(from.Str("address")).AppendLine(">")
                .Append("To: ").AppendLine(string.Join(", ", message.Items("toRecipients").Select(r => r.Obj("emailAddress").Str("address"))))
                .Append("Date: ").AppendLine(message.Str("receivedDateTime"))
                .Append("Subject: ").AppendLine(message.Str("subject"));
            if (message.TryGetProperty("hasAttachments", out var attachments) && attachments.ValueKind == JsonValueKind.True) sb.AppendLine("Has attachments.");
            return sb.AppendLine().Append(ConnectorHttp.Clip(body, 12_000)).ToString();
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private static object Message(string to, string? cc, string subject, string body)
    {
        var recipients = ConnectorHttp.Addresses(to, "To");
        if (recipients.Count == 0) throw new ConnectorException("Give at least one recipient.");
        return new
        {
            subject = ConnectorHttp.SingleLine(subject),
            body = new { contentType = "Text", content = body ?? string.Empty },
            toRecipients = Recipients(recipients),
            ccRecipients = Recipients(ConnectorHttp.Addresses(cc, "Cc")),
        };
    }

    private async Task<string> DraftAsync(
        [Description("Recipients, comma separated.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Plain-text body.")] string body,
        [Description("Optional copy recipients.")] string? cc = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var draft = await CallAsync(HttpMethod.Post, "messages", Message(to, cc, subject, body), cancellationToken).ConfigureAwait(false);
            return $"Draft saved in Outlook (id {draft?.Str("id")}). It was not sent.";
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
        [Description("Optional copy recipients.")] string? cc = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync(HttpMethod.Post, "sendMail", new { message = Message(to, cc, subject, body), saveToSentItems = true }, cancellationToken).ConfigureAwait(false);
            return $"E-mail sent to {to}.";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> ReplyAsync(
        [Description("Id of the message to answer.")] string id,
        [Description("Plain-text reply.")] string body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync(HttpMethod.Post, $"messages/{Uri.EscapeDataString(id)}/reply", new { comment = body }, cancellationToken).ConfigureAwait(false);
            return "Reply sent.";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>Category "outlook-calendar": read events freely; adding or deleting always asks.</summary>
internal sealed class OutlookCalendarToolProvider(MicrosoftAccount account, IHttpClientFactory httpClientFactory, TimeProvider time)
    : GraphTools(account, httpClientFactory), IAgentToolProvider
{
    public const string CategoryName = "outlook-calendar";

    public string Category => CategoryName;

    private TimeZoneInfo Zone => time.LocalTimeZone;

    private IReadOnlyDictionary<string, string> TimeZoneHeader => new Dictionary<string, string> { ["Prefer"] = $"outlook.timezone=\"{Zone.Id}\"" };

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(ListAsync, "outlook_list_events", "List events in the user's Outlook calendar for a period."), ToolRisk.Network, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(CreateAsync, "outlook_create_event", "Add an event to the user's Outlook calendar (attendees get invitations). The user approves it first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(DeleteAsync, "outlook_delete_event", "Delete an event from the user's Outlook calendar by id. The user approves it first."), ToolRisk.Mutating, CategoryName, ForceApproval: true);
    }

    private async Task<string> ListAsync(
        [Description("First day: today, tomorrow, or a date such as 2026-11-24.")] string from = "today",
        [Description("Number of days (1-62).")] int days = 7,
        [Description("Maximum events (1-50).")] int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var start = ConnectorTime.DayOrTime(from, time.GetUtcNow(), Zone);
            var end = start.AddDays(Math.Clamp(days, 1, 62));
            var result = await CallAsync(HttpMethod.Get,
                $"calendarView?startDateTime={Uri.EscapeDataString(ConnectorTime.Rfc3339(start))}&endDateTime={Uri.EscapeDataString(ConnectorTime.Rfc3339(end))}&$orderby=start/dateTime&$top={Math.Clamp(maxResults, 1, 50)}&$select=id,subject,start,end,location,isAllDay,attendees",
                null, cancellationToken, TimeZoneHeader).ConfigureAwait(false);
            var events = result is { } r ? r.Items("value").ToList() : [];
            if (events.Count == 0) return $"No events from {ConnectorTime.Describe(start, end, Zone)}.";

            var sb = new StringBuilder(ConnectorHttp.ExternalContentNote).AppendLine();
            foreach (var e in events)
            {
                bool allDay = e.TryGetProperty("isAllDay", out var a) && a.ValueKind == JsonValueKind.True;
                string when = allDay
                    ? e.Obj("start").Str("dateTime")[..Math.Min(10, e.Obj("start").Str("dateTime").Length)] + " (all day)"
                    : $"{Short(e.Obj("start").Str("dateTime"))}-{Short(e.Obj("end").Str("dateTime"))[^5..]}";
                sb.Append("- [").Append(e.Str("id")).Append("] ").Append(when).Append(" | ").Append(e.Str("subject"));
                if (e.Obj("location").Str("displayName").Length > 0) sb.Append(" | at ").Append(e.Obj("location").Str("displayName"));
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

    /// <summary>"2026-11-24T09:00:00.0000000" -> "Tue 24 Nov 09:00".</summary>
    private static string Short(string graphDateTime) =>
        DateTime.TryParse(graphDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)
            : graphDateTime;

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
            var begin = TimeZoneInfo.ConvertTime(ConnectorTime.Start(start, time.GetUtcNow(), Zone), Zone);
            var finish = begin.AddMinutes(Math.Clamp(durationMinutes, 5, 24 * 60 * 14));
            var guests = ConnectorHttp.Addresses(attendees, "attendees");
            var created = await CallAsync(HttpMethod.Post, "events", new
            {
                subject = ConnectorHttp.SingleLine(title),
                body = new { contentType = "Text", content = description ?? string.Empty },
                start = new { dateTime = begin.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = Zone.Id },
                end = new { dateTime = finish.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = Zone.Id },
                location = new { displayName = location ?? string.Empty },
                attendees = guests.Select(g => new { emailAddress = new { address = g }, type = "required" }).ToList(),
            }, cancellationToken).ConfigureAwait(false);
            return $"Event \"{title}\" added for {ConnectorTime.Describe(begin, finish, Zone)} (id {created?.Str("id")})" +
                   (guests.Count > 0 ? $"; invitations sent to {string.Join(", ", guests)}." : ".");
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> DeleteAsync([Description("Event id from outlook_list_events.")] string id, CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync(HttpMethod.Delete, $"events/{Uri.EscapeDataString(id)}", null, cancellationToken).ConfigureAwait(false);
            return $"Event {id} deleted.";
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }
}
