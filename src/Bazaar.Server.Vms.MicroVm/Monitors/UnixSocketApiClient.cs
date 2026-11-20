using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>HTTP over a Unix domain socket (HttpClient with a socket connect callback). Short timeouts and a response size cap.</summary>
public sealed class UnixSocketApiClient : IMicroVmApiClient
{
    private const int _maxResponseBytes = 1024 * 1024;
    private readonly TimeSpan _timeout;

    public UnixSocketApiClient(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<MicroVmApiResponse> SendAsync(string socketPath, MicroVmApiRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathRooted(socketPath))
        {
            throw new ArgumentException("The monitor socket path must be absolute.", nameof(socketPath));
        }

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = _timeout,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = _timeout, MaxResponseContentBufferSize = _maxResponseBytes };
        using var message = new HttpRequestMessage(request.Method, new Uri("http://localhost" + request.Path));
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new MicroVmApiResponse((int)response.StatusCode, body);
    }
}
