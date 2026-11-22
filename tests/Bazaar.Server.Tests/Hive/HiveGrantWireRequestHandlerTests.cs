using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Tests.Hive;

/// <summary>The wire adapter for <c>hive.grant.set</c> and <c>hive.grant.revoke</c>: decoding, the caller it builds, and the encoded reply.</summary>
public sealed class HiveGrantWireRequestHandlerTests
{
    private static readonly JsonWireCodec _codec = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeConnection(PrincipalRef principal) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = principal;
        public string DeviceId => "test";
        public string DeviceClass => "test";
        public IWireCodec Codec => _codec;
        public IReadOnlyList<StreamCursor> HelloCursors => [];
        public Bazaar.Server.Wire.Sync.ISubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class RecordingHandler : IHiveGrantWireHandler
    {
        public List<(string Type, HiveGrantChangeRequest Request, HiveCaller Caller)> Calls { get; } = [];

        public Task<HiveGrantChangeReply> HandleAsync(string wireType, HiveGrantChangeRequest request, HiveCaller caller, CancellationToken ct)
        {
            Calls.Add((wireType, request, caller));
            return Task.FromResult(new HiveGrantChangeReply(true, null, true));
        }
    }

    private static WireRequestContext Context<T>(string type, string kind, T payload) =>
        Context(type, kind, _codec.EncodePayload(payload));

    private static WireRequestContext Context(string type, string kind, ReadOnlyMemory<byte> payload) =>
        new(new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), kind)),
            new Envelope(1, type, EnvelopeKind.Request, "req-1", null, null, null, null, payload));

    [Theory]
    [InlineData(WireTypes.HiveGrantSet)]
    [InlineData(WireTypes.HiveGrantRevoke)]
    public async Task Owner_request_reaches_the_handler_as_the_owner_and_the_reply_is_encoded(string type)
    {
        var inner = new RecordingHandler();
        var request = new HiveGrantChangeRequest("a", "b", "procedure");
        var context = Context(type, "owner", request);

        var reply = await new HiveGrantWireRequestHandler(type, inner).HandleAsync(context, Ct);

        Assert.True(reply.IsOk);
        var call = Assert.Single(inner.Calls);
        Assert.Equal(type, call.Type);
        Assert.Equal(request, call.Request);
        Assert.True(call.Caller.IsOwner);
        Assert.Equal(context.Connection.Principal.Id.ToString("D"), call.Caller.Principal);
        var decoded = _codec.DecodePayload<HiveGrantChangeReply>(new Envelope(1, type, EnvelopeKind.Response, "req-1", "req-1", null, null, null, reply.Payload));
        Assert.Equal(new HiveGrantChangeReply(true, null, true), decoded);
    }

    [Fact]
    public async Task Agent_principal_is_not_the_owner()
    {
        var inner = new RecordingHandler();
        await new HiveGrantWireRequestHandler(WireTypes.HiveGrantSet, inner)
            .HandleAsync(Context(WireTypes.HiveGrantSet, "agent", new HiveGrantChangeRequest("a", "b", "procedure")), Ct);

        Assert.False(Assert.Single(inner.Calls).Caller.IsOwner);
    }

    [Fact]
    public async Task A_payload_that_does_not_fit_is_a_bad_request()
    {
        var inner = new RecordingHandler();
        var reply = await new HiveGrantWireRequestHandler(WireTypes.HiveGrantSet, inner)
            .HandleAsync(Context(WireTypes.HiveGrantSet, "owner", (ReadOnlyMemory<byte>)"\"not an object\""u8.ToArray()), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void Another_wire_type_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HiveGrantWireRequestHandler("board.move", new RecordingHandler()));
    }
}
