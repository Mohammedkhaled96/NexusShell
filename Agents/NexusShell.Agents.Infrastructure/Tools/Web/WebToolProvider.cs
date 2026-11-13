using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.Providers;

namespace NexusShell.Agents.Infrastructure.Tools.Web;

/// <summary>
/// Category "web": fetch a public web page as text. Network egress, so it asks
/// the user by default. SSRF guard: only http(s) on public addresses, checked for
/// every redirect hop (localhost, LAN and cloud metadata addresses are refused).
/// </summary>
internal sealed partial class WebToolProvider(IHttpClientFactory httpClientFactory) : IAgentToolProvider
{
    private const int MaxRedirects = 5;
    private const int MaxDownloadBytes = 3 * 1024 * 1024;

    public string Category => "web";

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new AgentToolDescriptor(
            AIFunctionFactory.Create(FetchAsync, "fetch_url", "Download a public web page or text file (http/https) and return its readable text."),
            ToolRisk.Network,
            Category);
    }

    private async Task<string> FetchAsync(
        [Description("Absolute http(s) URL.")] string url,
        [Description("Maximum characters to return.")] int maxChars = 20_000,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Invalid URL.";

        var client = httpClientFactory.CreateClient(HttpClientNames.WebTool);
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            string? refusal = await CheckPublicAsync(uri, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return refusal;

            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            if (response.Content.Headers.ContentLength > MaxDownloadBytes) return "The resource is too large to fetch.";

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (!mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                return $"Unsupported content type: {mediaType}";

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ? HtmlToText(body) : body;
            int limit = Math.Clamp(maxChars, 500, 100_000);
            return $"URL: {uri}\n\n" + (text.Length <= limit ? text : text[..limit] + "\n... (truncated)");
        }

        return "Too many redirects.";
    }

    internal static async Task<string?> CheckPublicAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "Only http and https URLs are allowed.";

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return $"Host not found: {uri.Host}";
        }

        return addresses.Length == 0 || addresses.Any(IsNonPublic)
            ? "Refused: the URL points to a local or private network address."
            : null;
    }

    internal static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast;

        byte[] b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254) // link-local + cloud metadata (169.254.169.254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
               || b[0] == 0
               || b[0] >= 224;
    }

    [GeneratedRegex(@"<(script|style|noscript|svg)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContentRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]*\n\s*\n\s*")]
    private static partial Regex BlankLinesRegex();

    internal static string HtmlToText(string html)
    {
        string text = NonContentRegex().Replace(html, " ");
        text = Regex.Replace(text, @"<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        return BlankLinesRegex().Replace(text, "\n\n").Trim();
    }
}
