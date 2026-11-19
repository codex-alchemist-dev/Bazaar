using System.Collections.Concurrent;
using System.Threading.Channels;
using Bazaar.Conduit.Relay;

namespace Bazaar.Conduit.Tests.Relay;

/// <summary>In-memory relay socket. Inbound messages are queued by the test; a null message means the relay closed.</summary>
internal sealed class FakeRelaySocket(Exception? connectError = null) : IRelaySocket
{
    private readonly Channel<byte[]?> _inbound = Channel.CreateUnbounded<byte[]?>();

    public Uri? ConnectedTo { get; private set; }
    public ConcurrentQueue<byte[]> Sent { get; } = new();
    public bool Closed { get; private set; }
    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectedTo = uri;
        if (connectError is not null)
        {
            throw connectError;
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        Sent.Enqueue(payload);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken) =>
        await _inbound.Reader.ReadAsync(cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        return Task.CompletedTask;
    }

    /// <summary>Simulates the relay dropping the connection.</summary>
    public void Drop() => _inbound.Writer.TryWrite(null);

    public void Dispose() => Disposed = true;
}

/// <summary>Hands out fake sockets and can make the next few connects fail.</summary>
internal sealed class FakeRelaySocketFactory
{
    private int _failConnects;

    public ConcurrentQueue<FakeRelaySocket> Created { get; } = new();

    public void FailNextConnects(int count) => Interlocked.Exchange(ref _failConnects, count);

    public FakeRelaySocket Create()
    {
        var fail = Interlocked.Decrement(ref _failConnects) >= 0;
        var socket = new FakeRelaySocket(fail ? new InvalidOperationException("relay refused") : null);
        Created.Enqueue(socket);
        return socket;
    }

    public FakeRelaySocket Latest() => Created.ToArray()[^1];
}

/// <summary>Polling helper for state that the module changes on its supervisor task.</summary>
internal static class RelayTestSupport
{
    public static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(5);
        }
    }
}
