using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Tests.Wire;

/// <summary>
/// Regression (chaos wire-fuzz): a <c>subscribe</c> request whose <c>streams</c> field is null or holds a null entry reached
/// the subscription engine and threw <see cref="NullReferenceException"/>, which the connection answered as
/// <c>err internal</c> (a server fault, retryable) and logged with a stack trace. A malformed payload must answer
/// <c>bad_request</c> and never reach the engine.
/// </summary>
public sealed class SubscribeHandlerMalformedTests
{
    private static readonly JsonWireCodec _codec = new();

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"streams":null}""")]
    [InlineData("""{"streams":[null]}""")]
    public async Task Malformed_subscribe_answers_bad_request(string payloadJson)
    {
        var request = new Envelope(1, WireTypes.Subscribe, EnvelopeKind.Request, "r1", null, null, null, null,
            System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var handler = new SubscribeHandler();

        var reply = await handler.HandleAsync(new WireRequestContext(new StubConnection(), request), TestContext.Current.CancellationToken);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    /// <summary>Minimal connection: the handler needs only the codec, and the guard runs before the subscription session is read.</summary>
    private sealed class StubConnection : IWireConnection
    {
        public Guid ConnectionId => Guid.Empty;

        public PrincipalRef Principal => new(Guid.Empty, "human");

        public string DeviceId => "stub";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public SubscriptionSession? Subscriptions => null;

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope) => true;

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }
}
