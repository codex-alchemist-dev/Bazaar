using Bazaar.Client.Core.Wire;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// Wraps a transport so a test can cut the live socket without telling the client: the next receive reports a close,
/// as a dropped network does. The client then reconnects through the same factory and resumes from its cursor.
/// </summary>
public sealed class SeveringTransport(IWireTransportFactory inner) : IWireTransportFactory
{
    private readonly Lock _gate = new();
    private SeverableConnection? _live;

    /// <summary>Number of connections opened so far.</summary>
    public int Opened { get; private set; }

    /// <inheritdoc />
    public async ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var conn = new SeverableConnection(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false));
        lock (_gate)
        {
            _live = conn;
            Opened++;
        }

        return conn;
    }

    /// <summary>Cuts the live socket. Returns false when none is open.</summary>
    public async ValueTask<bool> SeverAsync()
    {
        SeverableConnection? conn;
        lock (_gate) conn = _live;
        if (conn is null) return false;
        await conn.SeverAsync().ConfigureAwait(false);
        return true;
    }

    private sealed class SeverableConnection(IWireConnection inner) : IWireConnection
    {
        private readonly TaskCompletionSource _severed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask SeverAsync()
        {
            _severed.TrySetResult();
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
            _severed.Task.IsCompleted ? throw new InvalidOperationException("socket severed") : inner.SendAsync(frame, cancellationToken);

        public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_severed.Task.IsCompleted) return WireReceive.Closed();
            var receive = inner.ReceiveAsync(cancellationToken).AsTask();
            var winner = await Task.WhenAny(receive, _severed.Task.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (winner == receive && !_severed.Task.IsCompleted) return await receive.ConfigureAwait(false);
            _ = receive.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            return WireReceive.Closed();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
