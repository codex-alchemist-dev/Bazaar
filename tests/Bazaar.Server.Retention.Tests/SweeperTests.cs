using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Jobs;
using Bazaar.Server.Retention;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Retention.Tests;

public sealed class SweeperTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RetentionCategory _mail = new("mail", TimeSpan.FromDays(100), TimeSpan.FromDays(10), ErasureMethod.SoftDeleteThenPurge);

    private sealed class FakeStore(string category) : IRetentionStore
    {
        public List<RetentionRecordRef> Rows { get; } = [];
        public List<string> SoftDeleted { get; } = [];
        public List<string> Purged { get; } = [];
        public Action? BeforeList { get; set; }
        public string Category { get; } = category;

        public Task<IReadOnlyList<RetentionRecordRef>> ListSoftDeleteCandidatesAsync(DateTimeOffset createdBefore, int limit, CancellationToken ct)
        {
            BeforeList?.Invoke();
            return Task.FromResult<IReadOnlyList<RetentionRecordRef>>(Rows.Where(r => r.SoftDeletedAt is null && r.CreatedAt < createdBefore && !SoftDeleted.Contains(r.Id)).Take(limit).ToList());
        }

        public Task<IReadOnlyList<RetentionRecordRef>> ListPurgeCandidatesAsync(DateTimeOffset softDeletedBefore, int limit, CancellationToken ct)
        {
            BeforeList?.Invoke();
            return Task.FromResult<IReadOnlyList<RetentionRecordRef>>(Rows
                .Where(r => (r.SoftDeletedAt is not null || SoftDeleted.Contains(r.Id)) && !Purged.Contains(r.Id) && (r.SoftDeletedAt ?? _now) < softDeletedBefore)
                .Take(limit).ToList());
        }

        public Task SoftDeleteAsync(IReadOnlyList<string> ids, DateTimeOffset at, CancellationToken ct) { SoftDeleted.AddRange(ids); return Task.CompletedTask; }

        public Task PurgeAsync(IReadOnlyList<string> ids, CancellationToken ct) { Purged.AddRange(ids); return Task.CompletedTask; }
    }

    private sealed class FakeHolds : ILegalHoldScopeReader
    {
        public List<ActiveLegalHold> Active { get; } = [];
        public Exception? Fail { get; set; }
        public int Reads { get; private set; }

        public Task<IReadOnlyList<ActiveLegalHold>> ListActiveAsync(CancellationToken ct = default)
        {
            Reads++;
            if (Fail is not null)
                throw Fail;
            return Task.FromResult<IReadOnlyList<ActiveLegalHold>>(Active.ToList());
        }
    }

    private sealed class FakeAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
    }

    private static (RetentionSweeper Sweeper, FakeHolds Holds, FakeAudit Audit) Build(FakeStore store, RetentionSweepOptions? options = null, params RetentionCategory[] rows)
    {
        var holds = new FakeHolds();
        var audit = new FakeAudit();
        var policy = new RetentionPolicyTable(rows.Length == 0 ? [_mail] : rows);
        var sweeper = new RetentionSweeper([store], policy, new RetentionPlanner(policy), holds, audit, new FakeTimeProvider(_now), options);
        return (sweeper, holds, audit);
    }

    private static RetentionRecordRef Old(string id, string? person = null, string? gc = null) => new(id, _now.AddDays(-200), null, person, gc);

    [Fact]
    public async Task An_expired_record_is_soft_deleted_in_one_run_and_purged_after_its_window()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a"));
        var (sweeper, _, audit) = Build(store);

        var first = Assert.Single(await sweeper.SweepAsync(Ct));
        Assert.Equal(1, first.SoftDeleted);
        Assert.Equal(0, first.Purged);
        Assert.Equal(["a"], store.SoftDeleted);
        Assert.Empty(store.Purged);
        Assert.Equal(RetentionSweeper.SweptKind, Assert.Single(audit.Entries).Kind);

        // A soft-deleted record whose window has ended is purged.
        store.Rows.Clear();
        store.Rows.Add(new RetentionRecordRef("b", _now.AddDays(-300), _now.AddDays(-11)));
        var second = Assert.Single(await sweeper.SweepAsync(Ct));
        Assert.Equal(1, second.Purged);
        Assert.Equal(["b"], store.Purged);
    }

    [Fact]
    public async Task A_soft_deleted_record_inside_its_window_is_not_purged()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(new RetentionRecordRef("a", _now.AddDays(-300), _now.AddDays(-5)));
        var (sweeper, _, _) = Build(store);

        await sweeper.SweepAsync(Ct);

        Assert.Empty(store.Purged);
    }

    [Theory]
    [InlineData("company", "anything")]
    [InlineData("category", "mail")]
    [InlineData("person", "11111111-1111-1111-1111-111111111111")]
    [InlineData("gc", "gc-7")]
    public async Task A_legal_hold_covering_the_record_blocks_soft_delete_and_purge(string kind, string value)
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a", person: "11111111-1111-1111-1111-111111111111", gc: "gc-7"));
        store.Rows.Add(new RetentionRecordRef("b", _now.AddDays(-300), _now.AddDays(-50), "11111111-1111-1111-1111-111111111111", "gc-7"));
        var (sweeper, holds, audit) = Build(store);
        holds.Active.Add(new ActiveLegalHold(Guid.NewGuid(), kind, value));

        var result = Assert.Single(await sweeper.SweepAsync(Ct));

        Assert.Empty(store.SoftDeleted);
        Assert.Empty(store.Purged);
        Assert.Equal(2, result.Held);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_hold_on_someone_else_does_not_block()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a", person: "11111111-1111-1111-1111-111111111111", gc: "gc-7"));
        store.Rows.Add(Old("b", person: "22222222-2222-2222-2222-222222222222", gc: "gc-8"));
        var (sweeper, holds, _) = Build(store);
        holds.Active.Add(new ActiveLegalHold(Guid.NewGuid(), "person", "22222222-2222-2222-2222-222222222222"));
        holds.Active.Add(new ActiveLegalHold(Guid.NewGuid(), "category", "gc_message"));

        await sweeper.SweepAsync(Ct);

        Assert.Equal(["a"], store.SoftDeleted);
    }

    [Fact]
    public async Task An_unknown_hold_scope_blocks_everything()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a"));
        var (sweeper, holds, _) = Build(store);
        holds.Active.Add(new ActiveLegalHold(Guid.NewGuid(), "galaxy", "andromeda"));

        await sweeper.SweepAsync(Ct);

        Assert.Empty(store.SoftDeleted);
    }

    [Fact]
    public async Task A_hold_created_between_batches_stops_the_next_batch()
    {
        var store = new FakeStore("mail");
        for (var i = 0; i < 4; i++)
            store.Rows.Add(Old($"r{i}"));
        var (sweeper, holds, _) = Build(store, new RetentionSweepOptions(BatchSize: 2, MaxBatchesPerStep: 10));
        var lists = 0;
        // The second listing sees a new company hold: it must be read before the second soft delete.
        store.BeforeList = () => { if (++lists == 2) holds.Active.Add(new ActiveLegalHold(Guid.NewGuid(), "company", "*")); };

        var result = Assert.Single(await sweeper.SweepAsync(Ct));

        Assert.Equal(2, store.SoftDeleted.Count);
        Assert.Equal(2, result.SoftDeleted);
        Assert.Equal(2, result.Held);
    }

    [Fact]
    public async Task A_hold_read_error_removes_nothing_and_leaks_only_the_exception_type()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a"));
        var (sweeper, holds, audit) = Build(store);
        holds.Fail = new InvalidOperationException("password=hunter2 host=db");

        var result = Assert.Single(await sweeper.SweepAsync(Ct));

        Assert.Equal(RetentionSweepOutcome.FailedClosed, result.Outcome);
        Assert.Empty(store.SoftDeleted);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(RetentionSweeper.FailedClosedKind, entry.Kind);
        Assert.Contains("InvalidOperationException", entry.Payload);
        Assert.DoesNotContain("hunter2", entry.Payload);
    }

    [Fact]
    public async Task Cancellation_is_not_treated_as_a_hold_error()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a"));
        var (sweeper, holds, audit) = Build(store);
        holds.Fail = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sweeper.SweepAsync(Ct));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_category_with_no_row_a_period_or_a_method_is_kept_and_never_listed()
    {
        foreach (var rows in new[]
        {
            Array.Empty<RetentionCategory>(),
            [new RetentionCategory("other", TimeSpan.FromDays(1), TimeSpan.Zero, ErasureMethod.SoftDeleteThenPurge)],
            [new RetentionCategory("mail", null, TimeSpan.Zero, ErasureMethod.SoftDeleteThenPurge)],
            [new RetentionCategory("mail", TimeSpan.FromDays(1), TimeSpan.Zero, ErasureMethod.None)],
            [new RetentionCategory("mail", TimeSpan.FromDays(1), TimeSpan.Zero, ErasureMethod.CryptoErase)],
        })
        {
            var store = new FakeStore("mail");
            store.Rows.Add(Old("a"));
            var holds = new FakeHolds();
            var policy = new RetentionPolicyTable(rows);
            var sweeper = new RetentionSweeper([store], policy, new RetentionPlanner(policy), holds, new FakeAudit(), new FakeTimeProvider(_now));

            var result = Assert.Single(await sweeper.SweepAsync(Ct));

            Assert.Equal(RetentionSweepOutcome.Kept, result.Outcome);
            Assert.Empty(store.SoftDeleted);
            Assert.Equal(0, holds.Reads);
        }
    }

    [Fact]
    public async Task A_second_run_with_nothing_due_is_a_no_op_with_no_audit_row()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a"));
        var (sweeper, _, audit) = Build(store);

        await sweeper.SweepAsync(Ct);
        audit.Entries.Clear();
        store.Rows.Clear();
        await sweeper.SweepAsync(Ct);

        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_run_is_bounded_by_the_batch_limit()
    {
        var store = new FakeStore("mail");
        for (var i = 0; i < 10; i++)
            store.Rows.Add(Old($"r{i}"));
        var (sweeper, _, _) = Build(store, new RetentionSweepOptions(BatchSize: 2, MaxBatchesPerStep: 3));

        var result = Assert.Single(await sweeper.SweepAsync(Ct));

        Assert.Equal(6, result.SoftDeleted);
    }

    [Fact]
    public async Task Audit_rows_carry_counts_and_the_category_only()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("secret-id-1", person: "11111111-1111-1111-1111-111111111111"));
        var (sweeper, _, audit) = Build(store);

        await sweeper.SweepAsync(Ct);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(RetentionSweeper.Actor, entry.Actor);
        Assert.DoesNotContain("secret-id-1", entry.Payload);
        Assert.DoesNotContain("1111", entry.Payload);
        Assert.Contains("\"category\":\"mail\"", entry.Payload);
    }

    [Fact]
    public async Task One_failing_store_does_not_stop_the_others()
    {
        var bad = new FakeStore("mail") { BeforeList = () => throw new InvalidOperationException("boom") };
        bad.Rows.Add(Old("a"));
        var good = new FakeStore("notes");
        good.Rows.Add(Old("n"));
        var policy = new RetentionPolicyTable([_mail, new RetentionCategory("notes", TimeSpan.FromDays(100), TimeSpan.FromDays(10), ErasureMethod.SoftDeleteThenPurge)]);
        var sweeper = new RetentionSweeper([bad, good], policy, new RetentionPlanner(policy), new FakeHolds(), new FakeAudit(), new FakeTimeProvider(_now));

        var results = await sweeper.SweepAsync(Ct);

        Assert.Equal(RetentionSweepOutcome.FailedClosed, results[0].Outcome);
        Assert.Equal(["n"], good.SoftDeleted);
    }

    [Fact]
    public void Matcher_ignores_person_and_gc_holds_for_records_not_tied_to_them()
    {
        var holds = new[] { new ActiveLegalHold(Guid.NewGuid(), "person", "x"), new ActiveLegalHold(Guid.NewGuid(), "gc", "y") };
        Assert.False(LegalHoldMatcher.Covers(holds, "mail", new RetentionRecordRef("a", _now, null)));
    }

    [Fact]
    public async Task The_job_handler_runs_the_sweep_and_the_component_registers_an_hourly_job()
    {
        var store = new FakeStore("mail");
        store.Rows.Add(Old("a"));
        var (sweeper, _, _) = Build(store);
        var handler = new RetentionSweepJobHandler(sweeper);

        await handler.HandleAsync(new JobRecord(1, RetentionSweepJobHandler.JobKind, null, 1, "{}"), Ct);
        Assert.Equal(["a"], store.SoftDeleted);

        var services = new ServiceCollection();
        new RetentionComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var job = provider.GetRequiredService<RecurringJob>();
        Assert.Equal(RetentionSweepJobHandler.JobKind, job.Kind);
        Assert.Equal(TimeSpan.FromHours(1), job.Interval);
        Assert.Contains(services, d => d.ServiceType == typeof(IJobHandler));
    }

    [Fact]
    public void Sweep_options_fall_back_to_defaults_on_bad_values()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Retention:Sweep:BatchSize"] = "0",
            ["Bazaar:Retention:Sweep:MaxBatchesPerStep"] = "7",
        }).Build();

        var options = RetentionComponent.ReadSweepOptions(config);

        Assert.Equal(500, options.BatchSize);
        Assert.Equal(7, options.MaxBatchesPerStep);
    }
}
