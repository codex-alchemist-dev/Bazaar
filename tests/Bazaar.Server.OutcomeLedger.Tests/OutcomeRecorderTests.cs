using Bazaar.Server.OutcomeLedger;
using Bazaar.Server.OutcomeLedger.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.OutcomeLedger.Tests;

public class OutcomeRecorderTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class Listener(bool throws = false) : IOutcomeListener
    {
        public List<Guid> Seen { get; } = [];

        public Task OnRecordedAsync(Guid outcomeId, OutcomeRecord resolved, CancellationToken ct)
        {
            Seen.Add(outcomeId);
            return throws ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private static OutcomeRecord Record(OutcomeKind kind = OutcomeKind.CiFirstRun, decimal x = 0m, string key = "github:1", decimal? w = null) =>
        new(kind, "org/repo#1", x, key, _t0, [new OutcomeAttribution("agent-a", "author", "csharp")], w);

    private static (OutcomeRecorder Recorder, InMemoryOutcomeLedgerStore Store, FakeTimeProvider Time) Build(params IOutcomeListener[] listeners)
    {
        var store = new InMemoryOutcomeLedgerStore();
        var time = new FakeTimeProvider(_t0);
        return (new OutcomeRecorder(store, new OutcomeWeights(), time, listeners), store, time);
    }

    [Fact]
    public async Task Default_weights_follow_the_doc_table()
    {
        var (recorder, store, _) = Build();
        await recorder.RecordAsync(Record(OutcomeKind.CiFirstRun, key: "a:1"), TestContext.Current.CancellationToken);
        await recorder.RecordAsync(Record(OutcomeKind.Revert, key: "a:2"), TestContext.Current.CancellationToken);

        Assert.Equal(0.5m, store.Rows[0].Record.W);
        Assert.Equal(0.5m, store.Rows[0].Record.Attributions[0].W);
        Assert.Equal(1.0m, store.Rows[1].Record.W);
    }

    [Fact]
    public async Task An_explicit_weight_wins()
    {
        var (recorder, store, _) = Build();
        await recorder.RecordAsync(Record(w: 0.25m), TestContext.Current.CancellationToken);
        Assert.Equal(0.25m, store.Rows[0].Record.W);
    }

    [Fact]
    public async Task A_replay_records_once_and_tells_listeners_once()
    {
        var listener = new Listener();
        var (recorder, store, _) = Build(listener);

        var first = await recorder.RecordAsync(Record(), TestContext.Current.CancellationToken);
        var second = await recorder.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.True(first.Recorded);
        Assert.False(second.Recorded);
        Assert.Null(second.OutcomeId);
        Assert.Single(store.Rows);
        Assert.Single(listener.Seen);
    }

    [Fact]
    public async Task A_failing_listener_does_not_fail_or_block_the_next()
    {
        var ok = new Listener();
        var (recorder, store, _) = Build(new Listener(throws: true), ok);

        var result = await recorder.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.True(result.Recorded);
        Assert.Single(store.Rows);
        Assert.Single(ok.Seen);
    }

    [Fact]
    public async Task Recorded_at_comes_from_the_clock()
    {
        var (recorder, store, time) = Build();
        time.Advance(TimeSpan.FromMinutes(5));
        await recorder.RecordAsync(Record(), TestContext.Current.CancellationToken);
        Assert.Equal(_t0.AddMinutes(5), store.Rows[0].RecordedAt);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task X_outside_zero_to_one_is_refused(double x)
    {
        var (recorder, store, _) = Build();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => recorder.RecordAsync(Record(x: (decimal)x), TestContext.Current.CancellationToken));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_record_needs_attributions_and_no_duplicates()
    {
        var (recorder, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordAsync(Record() with { Attributions = [] }, TestContext.Current.CancellationToken));
        var twice = new OutcomeAttribution("agent-a", "author", "csharp");
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordAsync(Record() with { Attributions = [twice, twice with { Role = "approver" }] }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_negative_weight_is_refused()
    {
        var (recorder, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => recorder.RecordAsync(Record(w: -1m), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dedupe_key_has_the_source_colon_id_shape()
    {
        Assert.Equal("github:delivery-8f2c", OutcomeDedupeKey.For("github", "delivery-8f2c"));
        Assert.Throws<ArgumentException>(() => OutcomeDedupeKey.For("", "x"));
    }
}
