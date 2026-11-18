using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Retention.Tests;

public sealed class RetentionSweeperTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeHolds _holds = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    private RetentionSweeper Sweeper(params FakeCategory[] categories) => new(categories, _holds, _holds, _audit, _time);

    [Fact]
    public async Task Indefinite_period_is_a_no_op_without_audit()
    {
        var c = new FakeCategory { Period = null, Remaining = 100 };
        var result = await Sweeper(c).SweepAsync(Ct);

        Assert.Equal(RetentionSweepOutcome.Indefinite, result[0].Outcome);
        Assert.Empty(c.Calls);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Purges_with_cutoff_now_minus_period_and_audits_counts_only()
    {
        var c = new FakeCategory { Remaining = 25, BatchSize = 10 };
        var result = await Sweeper(c).SweepAsync(Ct);

        Assert.Equal(25, result[0].Deleted);
        Assert.Equal(3, c.Calls.Count);
        Assert.All(c.Calls, call => Assert.Equal(_time.GetUtcNow() - TimeSpan.FromDays(30), call.Cutoff));
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(RetentionAuditKinds.Swept, entry.Kind);
        Assert.Equal(RetentionAuditKinds.Actor, entry.Actor);
        using var doc = JsonDocument.Parse(entry.Payload);
        var root = doc.RootElement;
        Assert.Equal("fake", root.GetProperty("category").GetString());
        Assert.Equal(25, root.GetProperty("deleted").GetInt64());
        Assert.Equal("none", root.GetProperty("hold").GetString());
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromDays(30), root.GetProperty("cutoff").GetDateTimeOffset());
    }

    [Fact]
    public async Task Run_is_bounded_and_the_rest_waits_for_the_next_run()
    {
        var c = new FakeCategory { Remaining = 10_000, BatchSize = 10 };
        var result = await Sweeper(c).SweepAsync(Ct);

        Assert.Equal(RetentionSweeper.MaxBatchesPerRun, c.Calls.Count);
        Assert.Equal(RetentionSweeper.MaxBatchesPerRun * 10L, result[0].Deleted);
    }

    [Fact]
    public async Task Second_run_with_nothing_left_deletes_nothing_and_writes_no_audit()
    {
        var c = new FakeCategory { Remaining = 5 };
        var sweeper = Sweeper(c);
        await sweeper.SweepAsync(Ct);
        _audit.Entries.Clear();

        var again = await sweeper.SweepAsync(Ct);

        Assert.Equal(0, again[0].Deleted);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Non_person_hold_blocks_every_category()
    {
        _holds.NonPerson = true;
        var a = new FakeCategory("a") { Remaining = 5 };
        var b = new FakeCategory("b") { Remaining = 5 };
        var results = await Sweeper(a, b).SweepAsync(Ct);

        Assert.All(results, r => Assert.Equal(RetentionSweepOutcome.Blocked, r.Outcome));
        Assert.Empty(a.Calls);
        Assert.Empty(b.Calls);
        Assert.Equal(2, _audit.Entries.Count);
        Assert.All(_audit.Entries, e => Assert.Equal(RetentionAuditKinds.Blocked, e.Kind));
        Assert.Contains("non_person", _audit.Entries[0].Payload);
    }

    [Fact]
    public async Task Person_hold_on_a_covered_person_blocks_only_that_category()
    {
        var held = Guid.NewGuid();
        _holds.HeldPersons.Add(held);
        var withPerson = new FakeCategory("with_person") { Remaining = 5 };
        withPerson.Persons.Add(Guid.NewGuid());
        withPerson.Persons.Add(held);
        var without = new FakeCategory("without") { Remaining = 5 };

        var results = await Sweeper(withPerson, without).SweepAsync(Ct);

        Assert.Equal(RetentionSweepOutcome.Blocked, results[0].Outcome);
        Assert.Equal(RetentionSweepOutcome.Swept, results[1].Outcome);
        Assert.Empty(withPerson.Calls);
        Assert.Contains("\"hold\":\"person\"", _audit.Entries[0].Payload);
    }

    [Fact]
    public async Task Hold_reader_error_sweeps_nothing_and_audits_the_type_not_the_message()
    {
        _holds.Throw = true;
        var c = new FakeCategory { Remaining = 5 };
        var result = await Sweeper(c).SweepAsync(Ct);

        Assert.Equal(RetentionSweepOutcome.FailedClosed, result[0].Outcome);
        Assert.Empty(c.Calls);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(RetentionAuditKinds.FailedClosed, entry.Kind);
        Assert.Contains("InvalidOperationException", entry.Payload);
        Assert.DoesNotContain("secret detail", entry.Payload);
    }

    [Fact]
    public async Task Cancellation_is_not_treated_as_a_hold_error()
    {
        using var cts = new CancellationTokenSource();
        var holds = new CancellingHolds();
        var sweeper = new RetentionSweeper([new FakeCategory { Remaining = 1 }], holds, holds, _audit, _time);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sweeper.SweepAsync(cts.Token));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task One_failing_category_does_not_hide_the_others_results()
    {
        var a = new FakeCategory("a") { Remaining = 3 };
        var b = new FakeCategory("b") { Remaining = 4 };
        var results = await Sweeper(a, b).SweepAsync(Ct);

        Assert.Equal([3L, 4L], results.Select(r => r.Deleted));
        Assert.Equal(2, _audit.Entries.Count);
    }

    [Fact]
    public async Task Job_handler_runs_the_sweep()
    {
        var c = new FakeCategory { Remaining = 2 };
        var handler = new RetentionSweepJobHandler(Sweeper(c));

        Assert.Equal("retention.sweep", handler.Kind);
        await handler.HandleAsync(new Bazaar.Server.Jobs.JobRecord(1, handler.Kind, null, 0, "{}"), Ct);

        Assert.Single(c.Calls);
    }

    private sealed class CancellingHolds : Bazaar.Erasure.Abstractions.ILegalHoldReader, Bazaar.Erasure.Abstractions.ILegalHoldScopeReader
    {
        public Task<bool> IsPersonHeldAsync(Guid personId, CancellationToken ct = default) => throw new OperationCanceledException();
        public Task<bool> HasActiveNonPersonHoldAsync(CancellationToken ct = default) => throw new OperationCanceledException();
    }
}
