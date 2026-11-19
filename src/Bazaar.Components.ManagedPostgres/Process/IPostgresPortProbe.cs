using System.Net.Sockets;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>Asks whether anything accepts TCP connections on an address and port. The controller uses it for readiness and for the held-port refusal.</summary>
public interface IPostgresPortProbe
{
    /// <summary>True when a connection was accepted. False when nothing listens. Cancellation is the caller's attempt timeout.</summary>
    Task<bool> IsAcceptingAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>The production probe: a real TCP connect.</summary>
public sealed class TcpPostgresPortProbe : IPostgresPortProbe
{
    public async Task<bool> IsAcceptingAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
