using System.Net;
using System.Net.Sockets;

namespace NexusShell.Agents.Infrastructure.Tools.Web;

/// <summary>
/// SocketsHttpHandler.ConnectCallback that connects only to public IP addresses.
/// Validating at connect time (not just before the request) defeats DNS-rebinding
/// attacks against the fetch_url tool.
/// </summary>
internal static class PublicOnlyConnectCallback
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0 || addresses.Any(WebToolProvider.IsNonPublic))
            throw new HttpRequestException("Refused: the host resolves to a local or private network address.");

        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
