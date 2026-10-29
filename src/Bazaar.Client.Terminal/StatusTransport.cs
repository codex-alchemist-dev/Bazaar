using Bazaar.Client.Core.Wire;

namespace Bazaar.Client.Terminal;

/// <summary>
/// Wraps a transport and reports connection state changes. Each change is reported once, so retries while the
/// server is down do not repeat the line.
/// </summary>
public sealed class StatusTransport(IWireTransportFactory inner, Action<string> report) : IWireTransportFactory
{
    private readonly Lock _gate = new();
    private bool? _up;

    /// <inheritdoc />
    public async ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        IWireConnection connection;
        try
        {
            connection = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetState(up: false);
            throw;
        }

        SetState(up: true);
        return new Watched(connection, this);
    }

    internal void SetState(bool up)
    {
        lock (_gate)
        {
            if (_up == up) return;
            _up = up;
            report(up ? "connected" : "disconnected, retrying");
        }
    }

    private sealed class Watched(IWireConnection inner, StatusTransport owner) : IWireConnection
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) => inner.SendAsync(frame, cancellationToken);

        public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
        {
            var received = await inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (received.IsClosed) owner.SetState(up: false);
            return received;
        }

        public async ValueTask DisposeAsync()
        {
            owner.SetState(up: false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
