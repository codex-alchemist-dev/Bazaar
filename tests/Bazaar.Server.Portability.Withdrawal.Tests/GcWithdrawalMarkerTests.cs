using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Portability.Withdrawal.Tests;

public class GcWithdrawalMarkerTests
{
    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-0000000000a0");
    private static readonly Guid _m1 = Guid.Parse("00000000-0000-7000-8000-0000000000b1");
    private static readonly Guid _m2 = Guid.Parse("00000000-0000-7000-8000-0000000000b2");
    private static readonly Guid _key = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
    private static readonly GcPrincipal _viewer = new(Guid.Parse("00000000-0000-7000-8000-0000000000d1"), PrincipalKind.Owner, "owner");

    private static GcEvent Message(long seq, Guid messageId, string job, string sourceKey) =>
        new(_gc, seq, Guid.CreateVersion7(), GcEventKinds.Message, AuthorKind.Owner, _viewer.Id, SourceTrust.External, false, null,
            messageId, 1, null, null, $"{{\"import_job\":\"{job}\",\"source_key\":\"{sourceKey}\",\"kind\":\"note\",\"title\":\"t\"}}", DateTimeOffset.UnixEpoch);

    private static GcMessageVersionRecord Version(long seq, Guid messageId, int version, byte tag, bool tombstone = false) =>
        new(seq, messageId, version, _viewer.Id, DateTimeOffset.UnixEpoch, tombstone, tombstone ? null : _key, tombstone ? null : [tag, 9], tombstone ? null : [tag, tag, tag]);

    private static (GcWithdrawalMarker Marker, FakeGcLogWriter Writer) Build(IReadOnlyList<GcEvent> events, IReadOnlyList<GcMessageVersionRecord> versions)
    {
        var writer = new FakeGcLogWriter();
        return (new GcWithdrawalMarker(writer, new FakeGcLogReader(events), new FakeVersionSource(versions), _gc, _viewer), writer);
    }

    [Fact]
    public async Task Writes_a_system_edit_that_carries_the_current_sealed_body_and_the_job_marker()
    {
        var (marker, writer) = Build(
            [Message(1, _m1, "job", "k1"), Message(2, _m2, "other", "k2")],
            [Version(1, _m1, 1, 1), Version(5, _m1, 2, 2), Version(2, _m2, 1, 3)]);

        await marker.MarkWithdrawnAsync("job", 0, ["k1"], CancellationToken.None);

        var row = Assert.Single(writer.Written);
        Assert.Equal(GcEventKinds.MessageEdit, row.Kind);
        Assert.Equal(AuthorKind.System, row.AuthorKind);
        Assert.Equal(Guid.Empty, row.AuthorId);
        Assert.Equal(SourceTrust.System, row.SourceTrust);
        Assert.Equal("job", row.WithdrawnByImport);
        Assert.Equal(_m1, row.MessageId);
        Assert.Equal(3, row.Version);
        Assert.Equal(new byte[] { 2, 2, 2 }, row.Message!.BodyCiphertext.ToArray());
        Assert.Equal(new byte[] { 2, 9 }, row.Message.Nonce.ToArray());
        Assert.Equal(_key, row.Message.KeyId);
    }

    [Fact]
    public async Task A_repeat_writes_nothing_new()
    {
        var (marker, writer) = Build([Message(1, _m1, "job", "k1")], [Version(1, _m1, 1, 1)]);

        await marker.MarkWithdrawnAsync("job", 0, ["k1"], CancellationToken.None);
        await marker.MarkWithdrawnAsync("job", 0, ["k1"], CancellationToken.None);

        Assert.Single(writer.Written);
    }

    [Fact]
    public async Task A_tombstoned_message_is_skipped()
    {
        var (marker, writer) = Build([Message(1, _m1, "job", "k1")], [Version(1, _m1, 1, 1), Version(4, _m1, 2, 0, tombstone: true)]);

        await marker.MarkWithdrawnAsync("job", 0, ["k1"], CancellationToken.None);

        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task An_unknown_source_key_fails_before_anything_is_written()
    {
        var (marker, writer) = Build([Message(1, _m1, "job", "k1")], [Version(1, _m1, 1, 1)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => marker.MarkWithdrawnAsync("job", 0, ["k1", "gone"], CancellationToken.None));

        Assert.Contains("gone", ex.Message);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_writer_failure_propagates()
    {
        var (marker, writer) = Build([Message(1, _m1, "job", "k1")], [Version(1, _m1, 1, 1)]);
        writer.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => marker.MarkWithdrawnAsync("job", 0, ["k1"], CancellationToken.None));
    }

    [Fact]
    public async Task Resumes_after_a_partial_failure_through_the_withdrawer()
    {
        var events = new[] { Message(1, _m1, "job", "k1"), Message(2, _m2, "job", "k2") };
        var versions = new[] { Version(1, _m1, 1, 1), Version(2, _m2, 1, 2) };
        var (marker, writer) = Build(events, versions);
        var failing = new FailOnceMarker(marker, failOnBatch: 1);
        var target = new FakeTarget(["k1", "k2"]);
        var store = new FakeWithdrawals();
        var audit = new RecordingAuditSink();

        var first = await ImportWithdrawer.WithdrawAsync("job", target, store.Writer(failing), audit, 1, TimeProvider.System, CancellationToken.None);
        Assert.False(first.Succeeded);
        Assert.Single(writer.Written);

        var second = await ImportWithdrawer.WithdrawAsync("job", target, store.Writer(failing), audit, 1, TimeProvider.System, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(2, writer.Written.Count);
        Assert.Equal([_m1, _m2], writer.Written.Select(r => r.MessageId!.Value).Order());
    }

    private sealed class FailOnceMarker(IGcWithdrawalMarker inner, int failOnBatch) : IGcWithdrawalMarker
    {
        private bool _failed;

        public async Task MarkWithdrawnAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
        {
            if (!_failed && batchIndex == failOnBatch)
            {
                _failed = true;
                throw new IOException("transient");
            }
            await inner.MarkWithdrawnAsync(jobId, batchIndex, sourceKeys, ct);
        }
    }

    private sealed class FakeTarget(string[] gcKeys) : IImportTargetWriter
    {
        public Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) => throw new NotSupportedException();

        public Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(gcKeys);

        public Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Stands in for the withdrawal record: a key is recorded after its batch succeeded.</summary>
    private sealed class FakeWithdrawals
    {
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);

        public IImportWithdrawalWriter Writer(IGcWithdrawalMarker gc) => new Impl(this, gc);

        private sealed class Impl(FakeWithdrawals owner, IGcWithdrawalMarker gc) : IImportWithdrawalWriter
        {
            public Task<IReadOnlySet<string>> ReadWithdrawnSourceKeysAsync(string jobId, CancellationToken ct) =>
                Task.FromResult<IReadOnlySet<string>>(owner._done.ToHashSet(StringComparer.Ordinal));

            public async Task WithdrawGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
            {
                await gc.MarkWithdrawnAsync(jobId, batchIndex, sourceKeys, ct);
                owner._done.UnionWith(sourceKeys);
            }

            public Task RevertVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
