using Bazaar.Contracts.Gc;
using Bazaar.LoopGuard.Abstractions;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.LoopGuard;
using Bazaar.Server.Tests.Gc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Tests.LoopGuard;

/// <summary>
/// A loop-guard trip on an agent post (docs/design/06 "Loop guards", C34): a <c>guard_event</c> row, one system notice in the GC,
/// and the Psych Ward hand-off, each independent of the others.
/// </summary>
public sealed class LoopTripRecorderTests(GcDb db) : IClassFixture<GcDb>
{
    private sealed class NullNotifier : IGcNotifier
    {
        public void Notify(Guid gcId, long seq)
        {
        }
    }

    private sealed class RecordingGate(bool fail = false) : IPsychWardGate
    {
        public List<LoopTrip> Admitted { get; } = [];

        public Task AdmitAsync(LoopTrip trip, CancellationToken cancellationToken)
        {
            Admitted.Add(trip);
            return fail ? throw new InvalidOperationException("ward down") : Task.CompletedTask;
        }
    }

    private LoopTripRecorder Recorder(IPsychWardGate gate, FakeTimeProvider time) =>
        new(db.DataSource, new GcLogWriter(db.DataSource, new NullNotifier()), gate, NullLogger<LoopTripRecorder>.Instance, time);

    private async Task<long> CountAsync(string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static LoopTrip Trip(Guid gc, params Guid[] agents) => new(gc, "ping_pong", "two agents alternate", [.. agents.Order()]);

    [Fact]
    public async Task A_trip_writes_the_guard_event_the_system_notice_and_admits_the_agents()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var gate = new RecordingGate();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        await Recorder(gate, new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1))).HandleAsync(Trip(gc, a, b), ct);

        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM guard_event WHERE gc_id = $1 AND kind = 'ping_pong'", gc));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'system' AND payload->>'action' = 'loop_guard_tripped' AND payload->>'kind' = 'ping_pong'", gc));
        Assert.Equal(Trip(gc, a, b).AgentIds, Assert.Single(gate.Admitted).AgentIds);
    }

    [Fact]
    public async Task The_same_trip_in_the_same_minute_announces_once_and_a_later_one_announces_again()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var recorder = Recorder(new RecordingGate(), time);
        var agent = Guid.CreateVersion7();

        await recorder.HandleAsync(Trip(gc, agent), ct);
        await recorder.HandleAsync(Trip(gc, agent), ct);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'system'", gc));

        time.Advance(TimeSpan.FromMinutes(2));
        await recorder.HandleAsync(Trip(gc, agent), ct);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'system'", gc));
    }

    [Fact]
    public async Task A_failing_psych_ward_does_not_lose_the_audit_row_or_the_notice()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var gate = new RecordingGate(fail: true);

        await Recorder(gate, new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1))).HandleAsync(Trip(gc, Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Single(gate.Admitted);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM guard_event WHERE gc_id = $1", gc));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'system'", gc));
    }

    [Fact]
    public async Task A_missing_gc_still_records_nothing_but_does_not_stop_the_ward_admission()
    {
        GcTestSkip.WithoutDatabase();
        var gate = new RecordingGate();

        // guard_event.gc_id references gc and the notice needs the gc row, so both steps fail and are logged; the ward still hears it.
        await Recorder(gate, new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1))).HandleAsync(Trip(Guid.CreateVersion7(), Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Single(gate.Admitted);
    }

    [Fact]
    public void The_notice_key_depends_on_kind_agents_and_minute()
    {
        var gc = Guid.CreateVersion7();
        var a = Guid.CreateVersion7();
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var key = LoopTripRecorder.NoticeKey(Trip(gc, a), at);

        Assert.Equal(key, LoopTripRecorder.NoticeKey(Trip(gc, a), at.AddSeconds(30)));
        Assert.NotEqual(key, LoopTripRecorder.NoticeKey(Trip(gc, a), at.AddMinutes(2)));
        Assert.NotEqual(key, LoopTripRecorder.NoticeKey(Trip(gc, a) with { Kind = "mention_depth" }, at));
        Assert.NotEqual(key, LoopTripRecorder.NoticeKey(Trip(gc, Guid.CreateVersion7()), at));
    }
}
