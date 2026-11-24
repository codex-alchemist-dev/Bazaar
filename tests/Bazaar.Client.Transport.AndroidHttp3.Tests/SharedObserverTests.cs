using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Tests;

public sealed class SharedObserverTests
{
    private sealed class OnlyClosed : IWireConnectionObserver
    {
        public bool Closed { get; private set; }

        public ValueTask OnClosedAsync(Guid connectionId, CancellationToken cancellationToken)
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ClientReportsAreOptionalAndServerHookStillWorks()
    {
        IWireConnectionObserver observer = new OnlyClosed();
        observer.OnNegotiated("h3");
        observer.OnNetworkEvent(new WireNetworkEvent(WireNetworkEventKind.Handover, DateTimeOffset.UnixEpoch));
        observer.OnReconnectRequired(WireReconnectReason.StreamCut, null);
        observer.OnFallback("grpc-h3", "x");
        await observer.OnClosedAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.True(((OnlyClosed)observer).Closed);
    }

    [Fact]
    public async Task NullObserverIgnoresEverything()
    {
        IWireConnectionObserver observer = NullWireConnectionObserver.Instance;
        observer.OnNegotiated("h3");
        await observer.OnClosedAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
    }
}
