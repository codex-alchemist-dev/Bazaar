using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Athanor.Wire.Tests;

public sealed class AthanorWireConnectionTests
{
    private static readonly EditorDocumentId _id = new("ws", "src/a.cs");

    /// <summary>A transport that never reaches the server: every connect attempt fails.</summary>
    private sealed class UnreachableTransport : IWireTransportFactory
    {
        public int Attempts { get; private set; }

        public ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            throw new IOException("server unreachable");
        }
    }

    private static (AthanorWireConnection Connection, UnreachableTransport Transport) Build(TimeSpan timeout)
    {
        var transport = new UnreachableTransport();
        var client = new WireClient(
            transport,
            new JsonWireCodec(),
            new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "test", DeviceClass = "test", ClientBuild = "test", Jitter = () => 0 },
            TimeProvider.System);
        return (new AthanorWireConnection(client, timeout), transport);
    }

    [Fact]
    public async Task Nothing_connects_until_the_first_request()
    {
        var (connection, transport) = Build(TimeSpan.FromMilliseconds(100));
        await using var _ = connection;

        Assert.False(connection.Started);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.Attempts);
    }

    [Fact]
    public async Task Open_against_an_unreachable_server_times_out_as_unavailable()
    {
        var (connection, transport) = Build(TimeSpan.FromMilliseconds(150));
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<EditorFileException>(() => connection.Source.OpenAsync(_id, TestContext.Current.CancellationToken));

        Assert.Equal(EditorErrorCodes.Unavailable, error.Code);
        Assert.True(connection.Started);
        Assert.True(transport.Attempts >= 1);
    }

    [Fact]
    public async Task Save_against_an_unreachable_server_is_unavailable_not_a_throw()
    {
        var (connection, _) = Build(TimeSpan.FromMilliseconds(150));
        await using var _d = connection;

        var outcome = await connection.Source.SaveAsync(_id, "text", "r1", TestContext.Current.CancellationToken);

        Assert.IsType<SaveOutcome.Unavailable>(outcome);
    }

    [Fact]
    public async Task The_callers_cancellation_still_propagates()
    {
        var (connection, _) = Build(TimeSpan.FromSeconds(30));
        await using var _d = connection;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.Source.OpenAsync(_id, cts.Token));
    }

    [Fact]
    public async Task Dispose_stops_the_loop()
    {
        var (connection, _) = Build(TimeSpan.FromMilliseconds(100));
        _ = await Assert.ThrowsAsync<EditorFileException>(() => connection.Source.OpenAsync(_id, TestContext.Current.CancellationToken));

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
