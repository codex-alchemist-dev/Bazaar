using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Reports;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Reports;

public sealed class ReportRunnerTests
{
    // The fake clock is 2026-11-22 12:00 UTC; the definition is due daily at 06:00 UTC.
    private static readonly DateTimeOffset _slot22 = ReportTestKit.Utc(2026, 11, 22, 6);
    private static readonly DateTimeOffset _slot21 = ReportTestKit.Utc(2026, 11, 21, 6);

    private sealed class Rig
    {
        public Rig(params IReportSink[] sinks)
        {
            Clock = new FakeTimeProvider(ReportTestKit.Now);
            Sinks = sinks.Length == 0 ? [new FakeSink("inclient")] : sinks;
            Runner = new ReportRunner(Inputs, Sinks, Journal, new ReportCatchUpOptions(), Clock);
        }

        public FakeTimeProvider Clock { get; }

        public FakeInputSource Inputs { get; } = new();

        public MemoryJournal Journal { get; } = new();

        public IReportSink[] Sinks { get; }

        public ReportRunner Runner { get; }

        public Task<ReportRunBatch> Run(ReportScheduleState state, CancellationToken? token = null) =>
            Runner.RunDueAsync(ReportTestKit.Definition(), state, token ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_due_report_is_built_delivered_and_journalled()
    {
        Rig rig = new();

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(_slot21));

        ReportRunResult run = Assert.Single(batch.Runs);
        Assert.Equal(ReportRunOutcome.Delivered, run.Outcome);
        Assert.Equal(_slot22, run.Slot);
        Assert.True(run.Journaled);
        Assert.Equal(new ReportScheduleState(_slot22), batch.State);
        Assert.Equal(ReportTestKit.Utc(2026, 11, 23, 6), batch.NextDue);
        Assert.False(batch.Faulted);

        FakeSink sink = (FakeSink)rig.Sinks[0];
        HealthReport delivered = Assert.Single(sink.Received);
        Assert.Equal(_slot21, delivered.PeriodStart);
        Assert.Equal(_slot22, delivered.PeriodEnd);
        Assert.Equal(HealthReportCodec.Digest(delivered), run.Digest);
        Assert.Equal((_slot21, _slot22), Assert.Single(rig.Inputs.Calls));
    }

    [Fact]
    public async Task Run_records_form_a_valid_hash_chain_and_hold_identifiers_only()
    {
        Rig rig = new();

        await rig.Run(new ReportScheduleState(ReportTestKit.Utc(2026, 11, 19, 6)));

        Assert.Equal(3, rig.Journal.Records.Count);
        Assert.True(JournalChain.Verify(rig.Journal.Records).IsValid);
        Assert.All(rig.Journal.Records, record => Assert.Equal(ReportRunner.JournalKind, record.Kind));
        Assert.Equal(3, rig.Runner.NextSequence);
        Assert.Equal(rig.Journal.Records[^1].Hash, rig.Runner.PreviousHash);

        string payload = System.Text.Encoding.UTF8.GetString(rig.Journal.Records[0].Payload);
        Assert.Contains("\"definitionId\":\"daily_owner\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"delivered\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"inclient\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runner_can_continue_an_existing_chain()
    {
        Rig first = new();
        await first.Run(new ReportScheduleState(_slot21));

        MemoryJournal journal = new();
        ReportRunner second = new(
            new FakeInputSource(),
            [new FakeSink("inclient")],
            journal,
            new ReportCatchUpOptions(),
            new FakeTimeProvider(ReportTestKit.Now.AddDays(1)),
            first.Runner.NextSequence,
            first.Runner.PreviousHash);
        await second.RunDueAsync(ReportTestKit.Definition(), new ReportScheduleState(_slot22), CancellationToken.None);

        Assert.True(JournalChain.Verify([.. first.Journal.Records, .. journal.Records]).IsValid);
    }

    [Fact]
    public async Task Catch_up_is_bounded_and_reports_the_skipped_count()
    {
        Rig rig = new();

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(ReportTestKit.Utc(2026, 11, 16, 6)));

        Assert.Equal(3, batch.Runs.Count);
        Assert.Equal(3, batch.Skipped);
        Assert.Equal([ReportTestKit.Utc(2026, 11, 20, 6), _slot21, _slot22], batch.Runs.Select(r => r.Slot));
        Assert.Equal(new ReportScheduleState(_slot22), batch.State);
    }

    [Fact]
    public async Task A_second_run_with_the_returned_state_does_nothing()
    {
        Rig rig = new();
        ReportRunBatch first = await rig.Run(new ReportScheduleState(_slot21));

        ReportRunBatch second = await rig.Run(first.State);

        Assert.Empty(second.Runs);
        Assert.Single(((FakeSink)rig.Sinks[0]).Received);
    }

    [Fact]
    public async Task Nothing_is_built_for_a_schedule_that_was_just_armed()
    {
        Rig rig = new();

        ReportRunBatch batch = await rig.Run(ReportScheduleState.Armed(ReportTestKit.Now));

        Assert.Empty(batch.Runs);
        Assert.Empty(rig.Journal.Records);
    }

    [Fact]
    public async Task A_sink_that_throws_or_declines_does_not_stop_the_others()
    {
        FakeSink bad = new("bad", (_, _) => throw new InvalidOperationException("boom"));
        FakeSink no = new("no", (_, _) => ValueTask.FromResult(false));
        FakeSink good = new("good");
        Rig rig = new(bad, no, good);

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(_slot21));

        ReportRunResult run = Assert.Single(batch.Runs);
        Assert.Equal(ReportRunOutcome.Delivered, run.Outcome);
        Assert.Equal(
            [ReportSinkOutcome.Failed, ReportSinkOutcome.Rejected, ReportSinkOutcome.Accepted],
            run.Sinks.Select(s => s.Outcome));
        Assert.Single(good.Received);
    }

    [Fact]
    public async Task A_sink_that_hangs_times_out()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSink hang = new("hang", async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        Rig rig = new(hang);

        Task<ReportRunBatch> run = rig.Run(new ReportScheduleState(_slot21));
        await started.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        ReportRunBatch batch = await run;

        ReportRunResult result = Assert.Single(batch.Runs);
        Assert.Equal(ReportSinkOutcome.TimedOut, Assert.Single(result.Sinks).Outcome);
        Assert.Equal(ReportRunOutcome.NotDelivered, result.Outcome);
        Assert.Equal(new ReportScheduleState(_slot22), batch.State);
    }

    [Fact]
    public async Task With_no_sinks_the_run_is_journalled_as_not_delivered()
    {
        MemoryJournal journal = new();
        ReportRunner runner = new(new FakeInputSource(), [], journal, new ReportCatchUpOptions(), new FakeTimeProvider(ReportTestKit.Now));

        ReportRunBatch batch = await runner.RunDueAsync(ReportTestKit.Definition(), new ReportScheduleState(_slot21), CancellationToken.None);

        Assert.Equal(ReportRunOutcome.NotDelivered, Assert.Single(batch.Runs).Outcome);
        Assert.Single(journal.Records);
    }

    [Fact]
    public async Task A_bad_sink_id_is_recorded_as_invalid()
    {
        Rig rig = new(new ThrowingIdSink(), new FakeSink("has spaces"));

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(_slot21));

        Assert.All(Assert.Single(batch.Runs).Sinks, s => Assert.Equal("invalid", s.SinkId));
    }

    [Fact]
    public async Task An_input_failure_keeps_the_slot_and_stops_later_ones()
    {
        Rig rig = new();
        rig.Inputs.Handler = end => end == _slot21 ? throw new IOException("db down") : ValueTask.FromResult(ReportTestKit.Inputs());

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(ReportTestKit.Utc(2026, 11, 20, 6)));

        ReportRunResult run = Assert.Single(batch.Runs);
        Assert.Equal(ReportRunOutcome.InputFailed, run.Outcome);
        Assert.Equal(_slot21, run.Slot);
        Assert.Equal(new ReportScheduleState(ReportTestKit.Utc(2026, 11, 20, 6)), batch.State);
        Assert.Empty(((FakeSink)rig.Sinks[0]).Received);
        Assert.True(run.Journaled);
    }

    [Fact]
    public async Task Inputs_that_cannot_be_built_close_the_slot()
    {
        Rig rig = new();
        rig.Inputs.Handler = _ => ValueTask.FromResult(ReportTestKit.Inputs(ReportTestKit.Snapshot(score: 500)));

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(_slot21));

        Assert.Equal(ReportRunOutcome.BuildFailed, Assert.Single(batch.Runs).Outcome);
        Assert.Equal(new ReportScheduleState(_slot22), batch.State);
        Assert.Empty(((FakeSink)rig.Sinks[0]).Received);
    }

    [Fact]
    public async Task A_journal_failure_is_contained_and_leaves_the_chain_where_it_was()
    {
        Rig rig = new();
        rig.Journal.Fail = true;

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(_slot21));

        ReportRunResult run = Assert.Single(batch.Runs);
        Assert.Equal(ReportRunOutcome.Delivered, run.Outcome);
        Assert.False(run.Journaled);
        Assert.Equal(0, rig.Runner.NextSequence);
        Assert.Equal(JournalChain.GenesisHash(), rig.Runner.PreviousHash);

        rig.Journal.Fail = false;
        await rig.Run(new ReportScheduleState(ReportTestKit.Utc(2026, 11, 20, 6)));
        Assert.True(JournalChain.Verify(rig.Journal.Records).IsValid);
    }

    [Fact]
    public async Task Any_exception_from_the_input_source_is_an_input_failure()
    {
        Rig rig = new();
        ReportDefinition definition = ReportTestKit.Definition();
        rig.Inputs.Handler = _ => throw new InvalidOperationException();

        ReportRunBatch batch = await rig.Runner.RunDueAsync(definition, new ReportScheduleState(_slot21), CancellationToken.None);

        // An input source that throws anything is an input failure; the host sees no exception.
        Assert.Equal(ReportRunOutcome.InputFailed, Assert.Single(batch.Runs).Outcome);
        Assert.False(batch.Faulted);
    }

    [Fact]
    public async Task A_null_definition_is_faulted_not_thrown()
    {
        Rig rig = new();

        ReportRunBatch batch = await rig.Runner.RunDueAsync(null!, new ReportScheduleState(_slot21), CancellationToken.None);

        Assert.True(batch.Faulted);
        Assert.Empty(batch.Runs);
    }

    [Fact]
    public async Task A_state_outside_the_schedule_range_is_faulted_not_thrown()
    {
        Rig rig = new();

        ReportRunBatch batch = await rig.Run(new ReportScheduleState(new DateTimeOffset(9500, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.True(batch.Faulted);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates()
    {
        Rig rig = new();
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Run(new ReportScheduleState(_slot21), cancel.Token));
        Assert.Empty(rig.Journal.Records);

        // The runner is usable again afterwards.
        ReportRunBatch batch = await rig.Run(new ReportScheduleState(_slot21));
        Assert.Single(batch.Runs);
    }

    [Fact]
    public async Task An_overlapping_call_does_nothing_and_says_so()
    {
        Rig rig = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Inputs.Handler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return ReportTestKit.Inputs();
        };

        Task<ReportRunBatch> first = rig.Run(new ReportScheduleState(_slot21));
        await entered.Task;
        ReportRunBatch second = await rig.Run(new ReportScheduleState(_slot21));
        release.SetResult();
        ReportRunBatch done = await first;

        Assert.True(second.Busy);
        Assert.Empty(second.Runs);
        Assert.Single(done.Runs);
        Assert.Single(((FakeSink)rig.Sinks[0]).Received);
    }

    [Fact]
    public void Constructor_arguments_are_checked()
    {
        FakeTimeProvider clock = new(ReportTestKit.Now);

        Assert.Throws<ArgumentException>(() => new ReportRunner(new FakeInputSource(), [], new MemoryJournal(), new ReportCatchUpOptions(), clock, 0, new byte[5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportRunner(new FakeInputSource(), [], new MemoryJournal(), new ReportCatchUpOptions { MaxCatchUpRuns = 0 }, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportRunner(new FakeInputSource(), [], new MemoryJournal(), new ReportCatchUpOptions(), clock, -1));
    }
}
