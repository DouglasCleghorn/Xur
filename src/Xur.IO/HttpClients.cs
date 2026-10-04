using System.Net.Sockets;

namespace Xur.IO;

public static class HttpClients
{
    public static HttpClient Create(TimeSpan timeout, bool redirects = false) => new(new HttpRetryHandler(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })) { Timeout = timeout };

    public static HttpClient Unix(string socket, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (_, token) =>
            {
                var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), token);
                    return new NetworkStream(connection, ownsSocket: true);
                }
                catch { connection.Dispose(); throw; }
            }
        };
        return new HttpClient(new HttpRetryHandler(handler, maximumWait: TimeSpan.FromMilliseconds(500)))
            { BaseAddress = new Uri("http://localhost"), Timeout = timeout };
    }
}
