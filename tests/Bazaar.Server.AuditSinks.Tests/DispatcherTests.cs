using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AuditSinks.Tests;

public sealed class DispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AuditSinkDispatcher D, FakeSource Src, FakeCursorStore Cur, FakeAudit Audit, FakeTimeProvider Time) Build(
        IEnumerable<IAuditSink> sinks, AuditSinkDispatcherOptions? options = null)
    {
        var src = new FakeSource();
        var cur = new FakeCursorStore();
        var audit = new FakeAudit();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        return (new AuditSinkDispatcher(src, cur, sinks, audit, time, options), src, cur, audit, time);
    }

    [Fact]
    public async Task Delivers_rows_and_checkpoints_in_order_and_saves_cursor()
    {
        var sink = new RecordingSink("a");
        var (d, src, cur, _, _) = Build([sink], new AuditSinkDispatcherOptions { BatchSize = 2 });
        src.Rows.AddRange(Enumerable.Range(1, 5).Select(i => Make.Row(i)));
        src.Checkpoints.Add(Make.Checkpoint(1, 5));

        await d.RunOnceAsync(Ct);

        Assert.Equal([1L, 2, 3, 4, 5], sink.RowSeqs);
        Assert.Equal([5L], sink.CheckpointSeqs);
        Assert.Equal(new AuditSinkCursor(5, 5), cur.Cursors["a"]);
    }

    [Fact]
    public async Task Resumes_from_the_cursor_and_sends_nothing_twice()
    {
        var sink = new RecordingSink("a");
        var (d, src, cur, _, _) = Build([sink]);
        src.Rows.AddRange(Enumerable.Range(1, 3).Select(i => Make.Row(i)));
        cur.Cursors["a"] = new AuditSinkCursor(2, 0);

        await d.RunOnceAsync(Ct);
        await d.RunOnceAsync(Ct);

        Assert.Equal([3L], sink.RowSeqs);
    }

    [Fact]
    public async Task New_sink_starts_from_the_beginning_while_an_old_one_stays_current()
    {
        var old = new RecordingSink("old");
        var fresh = new RecordingSink("fresh");
        var (d, src, cur, _, _) = Build([old, fresh]);
        src.Rows.AddRange(Enumerable.Range(1, 3).Select(i => Make.Row(i)));
        cur.Cursors["old"] = new AuditSinkCursor(3, 0);

        await d.RunOnceAsync(Ct);

        Assert.Empty(old.RowSeqs);
        Assert.Equal([1L, 2, 3], fresh.RowSeqs);
    }

    [Fact]
    public async Task Failing_sink_is_isolated_and_audited_without_the_error_message()
    {
        var bad = new RecordingSink("bad") { Failures = 100 };
        var good = new RecordingSink("good");
        var (d, src, cur, audit, _) = Build([bad, good]);
        src.Rows.Add(Make.Row(1));

        await d.RunOnceAsync(Ct);

        Assert.Equal([1L], good.RowSeqs);
        Assert.Empty(bad.RowSeqs);
        Assert.False(cur.Cursors.ContainsKey("bad"));
        Assert.Equal(1, d.FailureCount("bad"));
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("audit.sink.failed", entry.Kind);
        Assert.Contains("\"sink\":\"bad\"", entry.Payload);
        Assert.DoesNotContain("secret-endpoint-token", entry.Payload);
    }

    [Fact]
    public async Task Backs_off_then_retries_and_audits_recovery()
    {
        var sink = new RecordingSink("a") { Failures = 2 };
        var (d, src, _, audit, time) = Build([sink], new AuditSinkDispatcherOptions { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(30) });
        src.Rows.Add(Make.Row(1));

        await d.RunOnceAsync(Ct);
        Assert.Equal(1, sink.Calls);

        await d.RunOnceAsync(Ct);
        Assert.Equal(1, sink.Calls);

        time.Advance(TimeSpan.FromSeconds(10));
        await d.RunOnceAsync(Ct);
        Assert.Equal(2, sink.Calls);

        time.Advance(TimeSpan.FromSeconds(19));
        await d.RunOnceAsync(Ct);
        Assert.Equal(2, sink.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        await d.RunOnceAsync(Ct);
        Assert.Equal([1L], sink.RowSeqs);
        Assert.Equal(0, d.FailureCount("a"));
        Assert.Equal(["audit.sink.failed", "audit.sink.failed", "audit.sink.recovered"], audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task Backoff_is_capped()
    {
        var sink = new RecordingSink("a") { Failures = 100 };
        var (d, src, _, _, time) = Build([sink], new AuditSinkDispatcherOptions { InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(4) });
        src.Rows.Add(Make.Row(1));

        for (var i = 0; i < 6; i++)
        {
            await d.RunOnceAsync(Ct);
            time.Advance(TimeSpan.FromSeconds(4));
        }

        Assert.Equal(6, sink.Calls);
    }

    [Fact]
    public async Task Failure_audit_that_throws_does_not_break_the_pass()
    {
        var bad = new RecordingSink("bad") { Failures = 1 };
        var good = new RecordingSink("good");
        var (d, src, _, audit, _) = Build([bad, good]);
        audit.Throw = true;
        src.Rows.Add(Make.Row(1));

        await d.RunOnceAsync(Ct);

        Assert.Equal([1L], good.RowSeqs);
        Assert.Equal(1, d.FailureCount("bad"));
    }

    [Fact]
    public async Task Crash_before_cursor_save_redelivers_with_the_same_idempotency_key()
    {
        var sink = new RecordingSink("a");
        var (d, src, cur, _, _) = Build([sink]);
        src.Rows.Add(Make.Row(1));
        await d.RunOnceAsync(Ct);
        var key = src.Rows[0].IdempotencyKey;

        cur.Cursors.Clear(); // simulate a lost cursor write
        await d.RunOnceAsync(Ct);

        Assert.Equal([1L, 1], sink.RowSeqs);
        Assert.Equal(key, Make.Row(1).IdempotencyKey);
        Assert.NotEqual(key, Make.Row(2).IdempotencyKey);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var (d, _, _, _, _) = Build([new RecordingSink("a")]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.RunOnceAsync(cts.Token));
    }

    [Fact]
    public void Duplicate_sink_names_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Build([new RecordingSink("a"), new RecordingSink("a")]));
    }
}
