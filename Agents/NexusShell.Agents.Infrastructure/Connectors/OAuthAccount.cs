using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Infrastructure.Providers;

namespace NexusShell.Agents.Infrastructure.Connectors;

/// <summary>Opens a web address in the user's default browser (replaceable in tests).</summary>
public interface IBrowserLauncher
{
    void Open(Uri address);
}

internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
}

/// <summary>A connector call failed; <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class ConnectorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything that differs between OAuth providers (Google, Microsoft).</summary>
internal sealed record OAuthProviderSettings(
    string Name,
    Uri AuthorizeEndpoint,
    Uri TokenEndpoint,
    string ClientIdVariable,
    string? ClientSecretVariable,
    string RefreshTokenVariable,
    IReadOnlyList<string> Scopes,
    string RedirectHost,
    IReadOnlyDictionary<string, string> ExtraAuthorizeParameters,
    Uri? RevokeEndpoint = null);

/// <summary>
/// OAuth 2.0 for a desktop app (RFC 8252): the provider's sign-in page opens in the user's own
/// browser (NexusShell never sees the password), the answer comes back to a one-time loopback
/// listener on this computer, and PKCE plus a state value protect the exchange. Only the refresh
/// token is kept, in a user environment variable; access tokens live in memory.
/// </summary>
internal class OAuthAccount(
    OAuthProviderSettings settings,
    ISecretStore secrets,
    IHttpClientFactory httpClientFactory,
    IBrowserLauncher browser,
    TimeProvider time,
    ILogger logger)
{
    internal static TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public OAuthProviderSettings Settings => settings;

    public bool HasClient =>
        secrets.HasSecret(new SecretReference(settings.ClientIdVariable))
        && (settings.ClientSecretVariable is null || secrets.HasSecret(new SecretReference(settings.ClientSecretVariable)));

    public bool IsSignedIn => secrets.HasSecret(new SecretReference(settings.RefreshTokenVariable));

    /// <summary>Opens the browser for sign-in and keeps the refresh token.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        string clientId = Secret(settings.ClientIdVariable) ?? throw new ConnectorException($"Paste the {settings.Name} client id first.");
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = LoopbackListener.Start();
        string redirect = $"http://{settings.RedirectHost}:{listener.Port}/";
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["scope"] = string.Join(' ', settings.Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        foreach (var (key, value) in settings.ExtraAuthorizeParameters) query[key] = value;

        var address = new Uri(settings.AuthorizeEndpoint + "?" + string.Join('&', query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
        logger.LogInformation("{Provider} sign-in opened in the browser.", settings.Name);
        browser.Open(address);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        IReadOnlyDictionary<string, string> answer;
        try
        {
            answer = await listener.WaitForCallbackAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectorException($"The {settings.Name} sign-in was not finished within {SignInTimeout.TotalMinutes:0} minutes. Press Sign in to try again.");
        }

        if (!answer.TryGetValue("state", out string? returnedState) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returnedState), Encoding.ASCII.GetBytes(state)))
            throw new ConnectorException("The sign-in answer did not match this request, so it was ignored. Press Sign in to try again.");
        if (answer.TryGetValue("error", out string? error))
            throw new ConnectorException($"{settings.Name} did not allow access: {answer.GetValueOrDefault("error_description") ?? error}.");
        if (!answer.TryGetValue("code", out string? code))
            throw new ConnectorException("The sign-in answer had no authorization code. Press Sign in to try again.");

        var tokens = await RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, cancellationToken).ConfigureAwait(false);

        if (tokens.RefreshToken is null)
            throw new ConnectorException($"{settings.Name} gave no lasting sign-in (refresh token). Remove NexusShell's access in your account settings and sign in again.");

        await secrets.SetSecretAsync(new SecretReference(settings.RefreshTokenVariable), tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        Cache(tokens);
        logger.LogInformation("{Provider} signed in.", settings.Name);
    }

    /// <summary>A valid access token (refreshed when expired or when <paramref name="forceRefresh"/>).</summary>
    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _accessToken is not null && time.GetUtcNow() < _expiresAt) return _accessToken;

            string refresh = Secret(settings.RefreshTokenVariable)
                ?? throw new ConnectorException($"{settings.Name} is not connected. Open Connectors and press Sign in.");
            OAuthTokens tokens;
            try
            {
                tokens = await RequestTokensAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                    ["scope"] = string.Join(' ', settings.Scopes),
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectorException ex) when (ex.Message.Contains("invalid_grant", StringComparison.Ordinal))
            {
                // Revoked or expired: forget it so the window shows "Sign in" again.
                await secrets.RemoveSecretAsync(new SecretReference(settings.RefreshTokenVariable), cancellationToken).ConfigureAwait(false);
                throw new ConnectorException($"The {settings.Name} sign-in expired or was revoked. Open Connectors and press Sign in again.", ex);
            }

            // Some providers (Microsoft) rotate refresh tokens.
            if (tokens.RefreshToken is { } rotated && rotated != refresh)
                await secrets.SetSecretAsync(new SecretReference(settings.RefreshTokenVariable), rotated, cancellationToken).ConfigureAwait(false);
            Cache(tokens);
            return tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        string? refresh = Secret(settings.RefreshTokenVariable);
        if (refresh is not null && settings.RevokeEndpoint is not null)
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh });
                using var response = await httpClientFactory.CreateClient(HttpClientNames.Connectors).PostAsync(settings.RevokeEndpoint, content, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "{Provider} token revocation failed; the local sign-in is removed anyway.", settings.Name);
            }
        }
        await secrets.RemoveSecretAsync(new SecretReference(settings.RefreshTokenVariable), cancellationToken).ConfigureAwait(false);
        _accessToken = null;
        logger.LogInformation("{Provider} signed out.", settings.Name);
    }

    private async Task<OAuthTokens> RequestTokensAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        form["client_id"] = Secret(settings.ClientIdVariable) ?? throw new ConnectorException($"Paste the {settings.Name} client id first.");
        if (settings.ClientSecretVariable is not null)
            form["client_secret"] = Secret(settings.ClientSecretVariable) ?? throw new ConnectorException($"Paste the {settings.Name} client secret first.");

        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClientFactory.CreateClient(HttpClientNames.Connectors).PostAsync(settings.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
        {
            string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "error" : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string description = root.TryGetProperty("error_description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            throw new ConnectorException($"{settings.Name} refused the sign-in ({error}). {FirstSentence(description)}".Trim());
        }

        return new OAuthTokens(
            access.GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            root.TryGetProperty("expires_in", out var x) && x.TryGetInt32(out int seconds) ? seconds : 3600);
    }

    private void Cache(OAuthTokens tokens)
    {
        _accessToken = tokens.AccessToken;
        _expiresAt = time.GetUtcNow().AddSeconds(Math.Max(60, tokens.ExpiresInSeconds - 60));
    }

    private string? Secret(string variable) => secrets.GetSecret(new SecretReference(variable));

    private static string FirstSentence(string text)
    {
        int end = text.IndexOfAny(['.', '\n', '\r']);
        return end < 0 ? text : text[..(end + 1)];
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record OAuthTokens(string AccessToken, string? RefreshToken, int ExpiresInSeconds);
}

/// <summary>
/// One-time HTTP listener on the loopback interface (127.0.0.1 and ::1, random port) that receives
/// the provider's redirect, answers with a short page and returns the query values.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _v4;
    private readonly TcpListener? _v6;

    private LoopbackListener(TcpListener v4, TcpListener? v6)
    {
        _v4 = v4;
        _v6 = v6;
    }

    public int Port => ((IPEndPoint)_v4.LocalEndpoint).Port;

    public static LoopbackListener Start()
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        TcpListener? v6 = null;
        try
        {
            v6 = new TcpListener(IPAddress.IPv6Loopback, ((IPEndPoint)v4.LocalEndpoint).Port);
            v6.Start();
        }
        catch (SocketException)
        {
            v6 = null; // no IPv6 loopback: "localhost" still resolves to 127.0.0.1
        }
        return new LoopbackListener(v4, v6);
    }

    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var accepts = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(cancellationToken).AsTask() };
            if (_v6 is not null) accepts.Add(_v6.AcceptTcpClientAsync(cancellationToken).AsTask());
            var accepted = await Task.WhenAny(accepts).ConfigureAwait(false);
            using var client = await accepted.ConfigureAwait(false);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                // headers are not needed
            }

            string target = requestLine?.Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
            var values = ParseQuery(target);
            bool isAnswer = values.ContainsKey("code") || values.ContainsKey("error");

            string page = isAnswer && !values.ContainsKey("error")
                ? Page("NexusShell is connected", "Sign-in finished. You can close this tab and go back to NexusShell.")
                : isAnswer
                    ? Page("Sign-in was not completed", "Access was not allowed. You can close this tab and try again in NexusShell.")
                    : Page("NexusShell", "Waiting for the sign-in.");
            byte[] bytes = Encoding.UTF8.GetBytes(page);
            string headers = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            if (isAnswer) return values; // other requests (favicon ...) are ignored
        }
    }

    internal static Dictionary<string, string> ParseQuery(string target)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int q = target.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) return values;
        foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            values[key] = value;
        }
        return values;
    }

    private static string Page(string title, string text) =>
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head>" +
        $"<body style=\"font-family:Segoe UI,sans-serif;background:#121212;color:#e0e0e0;padding:2em\"><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p></body></html>";

    public void Dispose()
    {
        _v4.Stop();
        _v6?.Stop();
    }
}
