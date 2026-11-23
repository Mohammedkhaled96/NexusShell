using System.Text.Json;

namespace NexusShell.Agents.Infrastructure.Connectors.Google;

/// <summary>
/// The file Google Cloud lets the user download for an OAuth client
/// ("client_secret_....apps.googleusercontent.com.json"). Importing it saves the user from typing
/// the client id and secret. The values go to user environment variables; the file is never copied.
/// </summary>
internal static class GoogleClientFile
{
    /// <summary>Where NexusShell looks for a freshly downloaded client file (replaceable in tests).</summary>
    internal static Func<string> DownloadsFolder { get; set; } =
        () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static readonly TimeSpan RecentEnough = TimeSpan.FromDays(14);

    public sealed record Client(string ClientId, string ClientSecret);

    /// <exception cref="ConnectorException">The file is not a Google Desktop app client file.</exception>
    public static Client Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ConnectorException("That file is not a Google client file. In Google Cloud, Clients, open your Desktop client and press Download JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("web", out _))
                throw new ConnectorException("That client is a Web application. Create a client with application type Desktop app, then download its file.");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("installed", out var installed))
                throw new ConnectorException("That file is not a Google Desktop app client file.");

            string id = installed.Str("client_id");
            string secret = installed.Str("client_secret");
            if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || secret.Length == 0)
                throw new ConnectorException("The client file has no client id or secret. Download it again from Google Cloud.");
            return new Client(id, secret);
        }
    }

    /// <summary>The newest client file downloaded in the last two weeks, or null.</summary>
    public static string? FindInDownloads()
    {
        string folder = DownloadsFolder();
        if (!Directory.Exists(folder)) return null;
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("client_secret_*.json", SearchOption.TopDirectoryOnly)
                .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < RecentEnough && f.Length < 64 * 1024)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
