using System.Text.Json;
using Bazaar.Contracts.Telemetry;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Diagnostics.Adapters;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Diagnostics.Tests.Adapters;

public sealed class CrashSubmitHandlerTests
{
    private sealed class FakeConnection(string deviceId, IWireCodec codec) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal { get; } = new(Guid.NewGuid(), "human");

        public string DeviceId => deviceId;

        public string DeviceClass => "windows";

        public IWireCodec Codec => codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public Bazaar.Server.Wire.Sync.SubscriptionSession? Subscriptions => null;

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope) => true;

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class MemoryRegistry : ICrashInstallRegistry
    {
        private readonly Dictionary<Guid, Guid> _bound = [];

        public Task<bool> TryBindAsync(Guid installId, Guid deviceId, bool isShared, CancellationToken ct = default) =>
            Task.FromResult(_bound.TryAdd(installId, deviceId) || _bound[installId] == deviceId);
    }

    private readonly InMemoryCrashStore _store = new();
    private readonly MemoryRegistry _registry = new();

    private CrashSubmitHandler Handler() => new(
        new CrashIntakeFactory(_store, new FakeSealer(), new RecordingAudit(), new ResolvedSymbols(), TimeProvider.System, new CrashFilingOptions { IssueRepo = "o/r" }),
        _registry,
        new InMemoryPendingAttachmentStore(new PendingAttachmentOptions(), TimeProvider.System));

    private static WireRequestContext Request(Guid device, byte[] payload) =>
        new(new FakeConnection(device.ToString("D"), new JsonWireCodec()), new Envelope(1, CrashWireTypes.Submit, EnvelopeKind.Request, "1", null, null, null, null, payload));

    private static byte[] Submit(CrashReport report) => JsonSerializer.SerializeToUtf8Bytes(new CrashSubmit(report), CrashContractsJsonContext.Default.CrashSubmit);

    [Fact]
    public async Task A_valid_report_binds_the_install_stores_and_answers_with_the_group_key()
    {
        var report = TestReports.Report();

        var reply = await Handler().HandleAsync(Request(Guid.NewGuid(), Submit(report)), TestContext.Current.CancellationToken);

        Assert.True(reply.IsOk);
        var ack = JsonSerializer.Deserialize(reply.Payload.Span, CrashContractsJsonContext.Default.CrashAck)!;
        Assert.Equal(report.ReportId, ack.ReportId);
        Assert.StartsWith("fp1:", ack.GroupKey, StringComparison.Ordinal);
        Assert.Single(_store.Reports);
    }

    [Fact]
    public async Task The_same_install_from_another_device_is_refused_and_stores_nothing()
    {
        var first = Guid.NewGuid();
        await Handler().HandleAsync(Request(first, Submit(TestReports.Report())), TestContext.Current.CancellationToken);

        var reply = await Handler().HandleAsync(Request(Guid.NewGuid(), Submit(TestReports.Report())), TestContext.Current.CancellationToken);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Single(_store.Reports);
    }

    [Fact]
    public async Task A_resend_of_the_same_report_gets_the_same_ack_without_a_second_row()
    {
        var device = Guid.NewGuid();
        var report = TestReports.Report();
        var first = await Handler().HandleAsync(Request(device, Submit(report)), TestContext.Current.CancellationToken);

        var second = await Handler().HandleAsync(Request(device, Submit(report)), TestContext.Current.CancellationToken);

        Assert.True(second.IsOk);
        Assert.Equal(first.Payload.ToArray(), second.Payload.ToArray());
        Assert.Single(_store.Reports);
    }

    [Fact]
    public async Task Garbage_and_a_non_guid_device_are_bad_requests()
    {
        var garbage = await Handler().HandleAsync(Request(Guid.NewGuid(), "{not json"u8.ToArray()), TestContext.Current.CancellationToken);
        var badDevice = await Handler().HandleAsync(
            new WireRequestContext(new FakeConnection("not-a-guid", new JsonWireCodec()), Request(Guid.NewGuid(), Submit(TestReports.Report())).Request),
            TestContext.Current.CancellationToken);

        Assert.Equal(WireErrorCodes.BadRequest, garbage.Error!.Code);
        Assert.Equal(WireErrorCodes.BadRequest, badDevice.Error!.Code);
        Assert.Empty(_store.Reports);
    }
}
