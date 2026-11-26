using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Postgres;
using Bazaar.Server.OfflineSync.Rules;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres.Tests;

public sealed class PostgresOfflineSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Store_keeps_outcomes_and_the_acked_sequence_across_instances()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var device = Guid.NewGuid();
        var write = Guid.NewGuid();
        await db.Store.RecordAsync(device, 3, new OfflineWriteOutcome(write, OfflineOutcomeKind.Conflict, "7", "why"), Ct);
        await db.Store.RecordAsync(device, 2, new OfflineWriteOutcome(Guid.NewGuid(), OfflineOutcomeKind.Applied), Ct);

        var fresh = new PostgresOfflineSyncStore(db.DataSource);

        Assert.Equal(3, await fresh.GetAckedSequenceAsync(device, Ct));
        Assert.Equal(0, await fresh.GetAckedSequenceAsync(Guid.NewGuid(), Ct));
        Assert.Equal(new OfflineWriteOutcome(write, OfflineOutcomeKind.Conflict, "7", "why"), await fresh.FindOutcomeAsync(device, write, Ct));
        Assert.Null(await fresh.FindOutcomeAsync(Guid.NewGuid(), write, Ct));
    }

    [Fact]
    public async Task A_replayed_batch_is_a_duplicate_and_changes_nothing()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var device = Guid.NewGuid();
        var batch = Make.Batch(device,
            Make.Write(device, 1, OfflineDataClass.GcMessage, "gc-1", Make.Message("a")),
            Make.Write(device, 2, OfflineDataClass.GcMessage, "gc-1", Make.Message("b")));

        var first = await db.Processor.ProcessAsync(batch, Ct);
        var again = await OfflineSyncDb.NewProcessor(db.DataSource).ProcessAsync(batch, Ct);

        Assert.Equal([OfflineOutcomeKind.Applied, OfflineOutcomeKind.Applied], first.Outcomes.Select(o => o.Kind));
        Assert.Equal(["1", "2"], first.Outcomes.Select(o => o.ServerVersion));
        Assert.Equal([OfflineOutcomeKind.Duplicate, OfflineOutcomeKind.Duplicate], again.Outcomes.Select(o => o.Kind));
        Assert.Equal(2, again.AckedSequence);
        Assert.Equal(2, await db.CountAsync("offline_gc_log"));
        Assert.Equal(2, await db.CountAsync("offline_merge_audit"));
    }

    [Fact]
    public async Task A_sequence_gap_stops_the_batch()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var device = Guid.NewGuid();

        var result = await db.Processor.ProcessAsync(Make.Batch(device,
            Make.Write(device, 1, OfflineDataClass.GcMessage, "gc-1", Make.Message("a")),
            Make.Write(device, 3, OfflineDataClass.GcMessage, "gc-1", Make.Message("c"))), Ct);

        Assert.Equal(2, result.GapAtSequence);
        Assert.Equal(1, result.AckedSequence);
        Assert.Equal(1, await db.CountAsync("offline_gc_log"));
    }

    [Fact]
    public async Task The_claim_and_the_state_change_roll_back_together()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var device = Guid.NewGuid();
        var write = Make.Write(device, 1, OfflineDataClass.GcMessage, "gc-1", Make.Message("a"));
        var failing = OfflineSyncDb.NewProcessor(db.DataSource, new FailAfterAppendRule(new PostgresGcAppendState(db.DataSource)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await failing.ProcessAsync(Make.Batch(device, write), Ct));

        Assert.Equal(0, await db.CountAsync("offline_write_claim"));
        Assert.Equal(0, await db.CountAsync("offline_gc_log"));
        Assert.Equal(0, await db.CountAsync("offline_gc_head"));
        Assert.Equal(0, await db.Store.GetAckedSequenceAsync(device, Ct));

        // The same write is not poisoned by the failed attempt: the retry applies it.
        var retry = await db.Processor.ProcessAsync(Make.Batch(device, write), Ct);
        Assert.Equal(OfflineOutcomeKind.Applied, retry.Outcomes.Single().Kind);
        Assert.Equal(1, await db.CountAsync("offline_gc_log"));
    }

    [Fact]
    public async Task State_changes_outside_a_write_scope_are_refused()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var state = new PostgresGcAppendState(db.DataSource);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await state.TryClaimWriteAsync(Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await state.AppendAsync("gc", new byte[] { 1 }, Ct));
        Assert.Equal(0, await db.CountAsync("offline_write_claim"));
    }

    [Fact]
    public async Task Gc_sequences_are_gapless_per_gc_under_parallel_devices()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var devices = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();

        var results = await Task.WhenAll(devices.Select(d => Task.Run(() => db.Processor.ProcessAsync(Make.Batch(d,
            [.. Enumerable.Range(1, 5).Select(i => Make.Write(d, i, OfflineDataClass.GcMessage, "gc-shared", Make.Message($"{d}-{i}")))]), Ct).AsTask(), Ct)));

        Assert.All(results, r => Assert.All(r.Outcomes, o => Assert.Equal(OfflineOutcomeKind.Applied, o.Kind)));
        await using var cmd = db.DataSource.CreateCommand("SELECT seq FROM offline_gc_log WHERE gc_id = 'gc-shared' ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var seqs = new List<long>();
        while (await reader.ReadAsync(Ct))
        {
            seqs.Add(reader.GetInt64(0));
        }

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), seqs);
    }

    [Fact]
    public async Task Concurrent_gc_edits_keep_both_versions_and_the_later_is_current()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var first = await db.Processor.ProcessAsync(Make.Batch(a, Make.Write(a, 1, OfflineDataClass.GcMessageEdit, "msg-1", Make.Message("a"), baseVersion: "1")), Ct);
        var second = await db.Processor.ProcessAsync(Make.Batch(b, Make.Write(b, 1, OfflineDataClass.GcMessageEdit, "msg-1", Make.Message("b"), baseVersion: "1")), Ct);

        Assert.Equal(OfflineOutcomeKind.Applied, first.Outcomes.Single().Kind);
        Assert.Equal(OfflineOutcomeKind.ConcurrentEdit, second.Outcomes.Single().Kind);
        Assert.Equal(2, await db.CountAsync("offline_gc_edit", "message_id = 'msg-1'"));
        var current = await new PostgresGcEditState(db.DataSource).GetCurrentAsync("msg-1", Ct);
        Assert.NotNull(current);
        Assert.Equal(long.Parse(second.Outcomes.Single().ServerVersion!, System.Globalization.CultureInfo.InvariantCulture), current.Sequence);
    }

    [Fact]
    public async Task Kanban_fields_apply_on_the_base_version_and_conflicts_open_a_review_item()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var applied = await db.Processor.ProcessAsync(Make.Batch(a, Make.Write(a, 1, OfflineDataClass.KanbanField, "task-1", Make.Field("title", "one"), baseVersion: "0")), Ct);
        var conflict = await db.Processor.ProcessAsync(Make.Batch(b, Make.Write(b, 1, OfflineDataClass.KanbanField, "task-1", Make.Field("title", "two"), baseVersion: "0")), Ct);
        var next = await db.Processor.ProcessAsync(Make.Batch(a, Make.Write(a, 2, OfflineDataClass.KanbanField, "task-1", Make.Field("title", "three"), baseVersion: "1")), Ct);

        Assert.Equal(("1", OfflineOutcomeKind.Applied), (applied.Outcomes.Single().ServerVersion, applied.Outcomes.Single().Kind));
        Assert.Equal(OfflineOutcomeKind.Conflict, conflict.Outcomes.Single().Kind);
        Assert.Equal("2", next.Outcomes.Single().ServerVersion);
        var snapshot = await new PostgresKanbanFieldState(db.DataSource).GetFieldAsync("task-1", "title", Ct);
        Assert.Equal(new KanbanFieldSnapshot("three", 2), snapshot);
        Assert.Equal(1, await db.CountAsync("offline_review_item", "kind = 'kanban-field-conflict' AND status = 'open'"));
    }

    [Fact]
    public async Task Concurrent_conflicting_kanban_field_writes_apply_exactly_one()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var devices = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();

        var results = await Task.WhenAll(devices.Select(d => Task.Run(() => db.Processor.ProcessAsync(Make.Batch(d,
            Make.Write(d, 1, OfflineDataClass.KanbanField, "task-race", Make.Field("title", d.ToString()), baseVersion: "0")), Ct).AsTask(), Ct)));

        var kinds = results.Select(r => r.Outcomes.Single().Kind).ToList();
        Assert.Equal(1, kinds.Count(k => k == OfflineOutcomeKind.Applied));
        Assert.Equal(devices.Length - 1, kinds.Count(k => k == OfflineOutcomeKind.Conflict));
        Assert.Equal(devices.Length - 1, await db.CountAsync("offline_review_item"));
    }

    [Fact]
    public async Task Opposite_kanban_edges_added_at_once_never_form_a_cycle()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);

        for (var round = 0; round < 5; round++)
        {
            var graph = $"graph-{round}";
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();

            var results = await Task.WhenAll(
                Task.Run(() => db.Processor.ProcessAsync(Make.Batch(a, Make.Write(a, 1, OfflineDataClass.KanbanEdge, graph, Make.Edge("x", "y"))), Ct).AsTask(), Ct),
                Task.Run(() => db.Processor.ProcessAsync(Make.Batch(b, Make.Write(b, 1, OfflineDataClass.KanbanEdge, graph, Make.Edge("y", "x"))), Ct).AsTask(), Ct));

            var kinds = results.Select(r => r.Outcomes.Single().Kind).Order().ToList();
            Assert.Equal([OfflineOutcomeKind.Applied, OfflineOutcomeKind.Rejected], kinds);
            var edges = await new PostgresKanbanEdgeState(db.DataSource).GetEdgesAsync(graph, Ct);
            Assert.Single(edges);
        }
    }

    [Fact]
    public async Task Kanban_edge_remove_needs_the_current_version()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var d = Guid.NewGuid();

        var add = await db.Processor.ProcessAsync(Make.Batch(d, Make.Write(d, 1, OfflineDataClass.KanbanEdge, "g", Make.Edge("x", "y"))), Ct);
        var stale = await db.Processor.ProcessAsync(Make.Batch(d, Make.Write(d, 2, OfflineDataClass.KanbanEdge, "g", Make.Edge("x", "y", remove: true), baseVersion: "0")), Ct);
        var remove = await db.Processor.ProcessAsync(Make.Batch(d, Make.Write(d, 3, OfflineDataClass.KanbanEdge, "g", Make.Edge("x", "y", remove: true), baseVersion: "1")), Ct);

        Assert.Equal("1", add.Outcomes.Single().ServerVersion);
        Assert.Equal(OfflineOutcomeKind.Conflict, stale.Outcomes.Single().Kind);
        Assert.Equal(OfflineOutcomeKind.Applied, remove.Outcomes.Single().Kind);
        Assert.Empty(await new PostgresKanbanEdgeState(db.DataSource).GetEdgesAsync("g", Ct));
    }

    [Fact]
    public async Task Last_writer_wins_gives_the_same_current_and_history_in_either_arrival_order()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var early = Make.Write(a, 1, OfflineDataClass.Preference, "pref-x", Make.Preference("early"), wall: Make.Now - 1000);
        var late = Make.Write(b, 1, OfflineDataClass.Preference, "pref-x", Make.Preference("late"), wall: Make.Now - 10);
        var lateFirst = Make.Write(b, 2, OfflineDataClass.Preference, "pref-y", Make.Preference("late"), wall: Make.Now - 10);
        var earlyAfter = Make.Write(a, 2, OfflineDataClass.Preference, "pref-y", Make.Preference("early"), wall: Make.Now - 1000);

        await db.Processor.ProcessAsync(Make.Batch(a, early), Ct);
        await db.Processor.ProcessAsync(Make.Batch(b, late), Ct);
        await db.Processor.ProcessAsync(Make.Batch(b, lateFirst), Ct);
        var lost = await db.Processor.ProcessAsync(Make.Batch(a, earlyAfter), Ct);

        var state = new PostgresLastWriterWinsState(db.DataSource);
        var x = await state.GetCurrentAsync("pref-x", Ct);
        var y = await state.GetCurrentAsync("pref-y", Ct);
        Assert.NotNull(x);
        Assert.NotNull(y);
        Assert.Equal(x.Version, y.Version);
        Assert.Equal(x.Payload.ToArray(), y.Payload.ToArray());
        Assert.Equal(OfflineOutcomeKind.Applied, lost.Outcomes.Single().Kind);
        Assert.Equal(1, await db.CountAsync("offline_lww_history", "key = 'pref-x'"));
        Assert.Equal(1, await db.CountAsync("offline_lww_history", "key = 'pref-y'"));
    }

    [Fact]
    public async Task Attachments_are_content_addressed_and_idempotent()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var d = Guid.NewGuid();
        var (bytes, hash) = Make.Blob("hello");

        var result = await db.Processor.ProcessAsync(Make.Batch(d,
            Make.Write(d, 1, OfflineDataClass.Attachment, hash, bytes),
            Make.Write(d, 2, OfflineDataClass.Attachment, hash, bytes)), Ct);

        Assert.All(result.Outcomes, o => Assert.Equal(OfflineOutcomeKind.Applied, o.Kind));
        Assert.Equal(1, await db.CountAsync("offline_attachment"));
    }

    [Fact]
    public async Task Audit_actions_get_gapless_arrival_positions_and_dedupe_on_the_write_id()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var d = Guid.NewGuid();
        var repeated = Make.Write(d, 1, OfflineDataClass.AuditAction, "audit", new byte[] { 1 });

        var first = await db.Processor.ProcessAsync(Make.Batch(d, repeated, Make.Write(d, 2, OfflineDataClass.AuditAction, "audit", new byte[] { 2 })), Ct);
        var again = await db.Processor.ProcessAsync(Make.Batch(d, repeated), Ct);

        Assert.Equal(["1", "2"], first.Outcomes.Select(o => o.ServerVersion));
        Assert.Equal(OfflineOutcomeKind.Duplicate, again.Outcomes.Single().Kind);
        Assert.Equal(2, await db.CountAsync("offline_audit_action"));
    }

    [Fact]
    public async Task Merge_audit_records_one_row_per_non_duplicate_outcome()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);
        var d = Guid.NewGuid();
        var write = Make.Write(d, 1, OfflineDataClass.Preference, "pref-a", Make.Preference("v"));

        await db.Processor.ProcessAsync(Make.Batch(d, write), Ct);
        await db.Processor.ProcessAsync(Make.Batch(d, write), Ct);

        Assert.Equal(1, await db.CountAsync("offline_merge_audit", $"write_id = '{write.WriteId}' AND outcome_kind = 1"));
    }

    [Fact]
    public async Task The_service_registration_replaces_the_in_memory_defaults_in_either_order()
    {
        RequireDatabase();
        await using var db = await OfflineSyncDb.CreateAsync(Ct);

        foreach (var postgresFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton(db.DataSource);
            if (postgresFirst)
            {
                services.AddBazaarOfflineSyncPostgres();
                services.AddBazaarOfflineSync();
            }
            else
            {
                services.AddBazaarOfflineSync();
                services.AddBazaarOfflineSyncPostgres();
            }

            await using var provider = services.BuildServiceProvider();

            Assert.IsType<PostgresOfflineSyncStore>(provider.GetRequiredService<IOfflineSyncStore>());
            Assert.IsType<PostgresOfflineMergeAudit>(provider.GetRequiredService<IOfflineMergeAudit>());
            Assert.IsType<PostgresOfflineWriteScopeFactory>(provider.GetRequiredService<IOfflineWriteScopeFactory>());
            Assert.IsType<PostgresGcAppendState>(provider.GetRequiredService<IGcAppendState>());
            Assert.IsType<PostgresKanbanEdgeState>(provider.GetRequiredService<IKanbanEdgeState>());
            Assert.IsType<PostgresAuditActionSink>(provider.GetRequiredService<IAuditActionSink>());
            Assert.NotNull(provider.GetRequiredService<OfflineSyncProcessor>());
        }
    }
}
