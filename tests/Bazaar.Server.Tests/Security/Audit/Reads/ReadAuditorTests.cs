using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit.Reads;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Security.Audit.Reads;

public class ReadAuditorTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] _expectedTags = ["agent", "external"];

    private static readonly ReadAccessContext _owner = new(
        "human:owner", "tool.gc.read", Session: "sess-1", Purpose: "catch_up", TaintTags: ["agent", "external", "agent"]);

    private static readonly ReadAccessContext _charles = new("agent:charles", "tool.kanban.list");

    private static (ReadAuditor Auditor, RecordingReadLog Log, FixedClock Clock) Build()
    {
        var log = new RecordingReadLog();
        var clock = new FixedClock(_t0);
        return (new ReadAuditor(log, clock), log, clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_read_outside_any_scope_is_written_at_once_as_unattributed()
    {
        var (auditor, log, _) = Build();

        await auditor.RecordAsync(new DataRead("gc_message", 3, "select", new string('a', 64)), Ct);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("data.read", entry.Kind);
        Assert.Equal("unattributed", entry.Actor);
        var payload = log.Payloads()[0];
        Assert.Equal("unattributed", payload["route"]!.GetValue<string>());
        Assert.Equal("gc_message", payload["object_type"]!.GetValue<string>());
        Assert.Equal(3, payload["result_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reads_in_a_scope_are_batched_into_one_row_per_query_shape_at_dispose()
    {
        var (auditor, log, _) = Build();
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);

        await using (var scope = auditor.BeginScope(_owner))
        {
            await auditor.RecordAsync(new DataRead("gc_message", 2, "select", hashA), Ct);
            await auditor.RecordAsync(new DataRead("gc_message", 3, "select", hashA), Ct);
            await auditor.RecordAsync(new DataRead("gc_event", 4, "select", hashB), Ct);
            Assert.Empty(log.Entries);
        }

        Assert.Equal(2, log.Entries.Count);
        var payloads = log.Payloads();
        Assert.Equal("gc_event", payloads[0]["object_type"]!.GetValue<string>());
        Assert.Equal(4, payloads[0]["result_count"]!.GetValue<int>());
        Assert.Equal("gc_message", payloads[1]["object_type"]!.GetValue<string>());
        Assert.Equal(5, payloads[1]["result_count"]!.GetValue<int>());
        Assert.All(log.Entries, e => Assert.Equal("human:owner", e.Actor));
        Assert.All(payloads, p => Assert.Equal("sess-1", p["session"]!.GetValue<string>()));
        Assert.All(payloads, p => Assert.Equal("catch_up", p["purpose"]!.GetValue<string>()));
        Assert.All(payloads, p => Assert.Equal(_expectedTags, p["taint_tags"]!.AsArray().Select(t => t!.GetValue<string>())));
    }

    [Fact]
    public async Task A_personal_object_read_is_written_at_once_with_its_id()
    {
        var (auditor, log, _) = Build();

        await using var scope = auditor.BeginScope(_owner);
        await auditor.RecordAsync(new DataRead("person_key", 1, "select", new string('c', 64), ObjectId: "person-42"), Ct);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("person-42", log.Payloads()[0]["object_id"]!.GetValue<string>());
        Assert.Equal("human:owner", entry.Actor);
    }

    [Fact]
    public async Task A_nested_scope_uses_its_own_context_and_the_outer_scope_resumes_after_it()
    {
        var (auditor, log, _) = Build();

        await using var outer = auditor.BeginScope(_owner);
        await using (auditor.BeginScope(_charles))
        {
            await auditor.RecordAsync(new DataRead("task", 1), Ct);
        }

        // The inner scope wrote its own row; the read after it belongs to the outer scope and is still batched.
        Assert.Equal("agent:charles", Assert.Single(log.Entries).Actor);
        await auditor.RecordAsync(new DataRead("gc", 2), Ct);
        Assert.Single(log.Entries);

        await outer.DisposeAsync();
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("human:owner", log.Entries[1].Actor);
    }

    [Fact]
    public async Task A_scope_with_no_reads_writes_nothing_and_dispose_is_idempotent()
    {
        var (auditor, log, _) = Build();

        var scope = auditor.BeginScope(_owner);
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Reads_from_parallel_tasks_in_one_scope_sum_into_one_row()
    {
        var (auditor, log, _) = Build();

        await using (auditor.BeginScope(_owner))
        {
            var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() => auditor.RecordAsync(new DataRead("gc_message", 1, "select", new string('d', 64)), Ct), Ct));
            await Task.WhenAll(tasks);
        }

        var entry = Assert.Single(log.Entries);
        Assert.Equal(50, log.Payloads()[0]["result_count"]!.GetValue<int>());
        Assert.Equal("human:owner", entry.Actor);
    }

    [Fact]
    public async Task The_scope_context_flows_across_an_await()
    {
        var (auditor, log, _) = Build();

        await using (auditor.BeginScope(_charles))
        {
            await Task.Yield();
            await auditor.RecordAsync(new DataRead("task", 1), Ct);
        }

        Assert.Equal("agent:charles", Assert.Single(log.Entries).Actor);
    }

    [Fact]
    public async Task A_batched_row_carries_the_first_and_last_read_times()
    {
        var (auditor, log, clock) = Build();

        await using (auditor.BeginScope(_charles))
        {
            await auditor.RecordAsync(new DataRead("task", 1), Ct);
            clock.Now = _t0.AddSeconds(5);
            await auditor.RecordAsync(new DataRead("task", 1), Ct);
        }

        var payload = log.Payloads()[0];
        Assert.Equal("2026-11-15T09:00:00.000000Z", payload["read_from"]!.GetValue<string>());
        Assert.Equal("2026-11-15T09:00:05.000000Z", payload["read_to"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_reads_and_contexts_are_refused_and_write_nothing()
    {
        var (auditor, log, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => auditor.RecordAsync(new DataRead("gc message", 1), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => auditor.RecordAsync(new DataRead("gc", -1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => auditor.RecordAsync(new DataRead("person_key", 2, ObjectId: "p1"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => auditor.RecordAsync(new DataRead("gc", 1, QueryShapeHash: "not-a-hash"), Ct));
        Assert.Throws<ArgumentException>(() => auditor.BeginScope(new ReadAccessContext("human: owner", "tool.gc.read")));
        Assert.Throws<ArgumentException>(() => auditor.BeginScope(new ReadAccessContext("human:owner", "Tool GC")));
        Assert.Throws<ArgumentException>(() => auditor.BeginScope(new ReadAccessContext("human:owner", "tool.gc.read", TaintTags: ["bad tag"])));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Rows_hold_ids_and_labels_only_and_never_the_statement_text()
    {
        var (auditor, log, _) = Build();
        const string sql = "select body from gc_message where body = 'the private message'";

        await auditor.RecordAsync(DataRead.FromSql("gc_message", sql, 1), Ct);

        var text = log.Entries[0].Payload;
        Assert.DoesNotContain("private message", text);
        Assert.DoesNotContain("where", text);
        Assert.Equal(JsonNode.Parse(text)!.ToJsonString(), text);
    }

    [Fact]
    public async Task The_persisted_payload_has_no_query_shape_hash_field_or_value()
    {
        // The shape hash keys the batch in memory only (owner decision on #594, item 3).
        var (auditor, log, _) = Build();
        var hash = QueryShape.Hash("select id from gc_message where seq > $1");

        await using (auditor.BeginScope(_owner))
        {
            await auditor.RecordAsync(new DataRead("gc_message", 2, "select", hash), Ct);
            await auditor.RecordAsync(new DataRead("gc_message", 3, "select", hash), Ct);
        }

        await auditor.RecordAsync(new DataRead("gc_message", 1, "select", hash, ObjectId: "person-1"), Ct);

        Assert.Equal(2, log.Entries.Count);
        foreach (var entry in log.Entries)
        {
            var payload = JsonNode.Parse(entry.Payload)!.AsObject();
            Assert.False(payload.ContainsKey("query_shape_hash"));
            Assert.DoesNotContain(hash, entry.Payload);
        }
        // The batch itself is still one row of five reads, so batching by shape is unchanged.
        Assert.Equal(5, log.Payloads()[0]["result_count"]!.GetValue<int>());
    }

    [Fact]
    public void The_service_registration_resolves_the_auditor_over_the_audit_log()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(new RecordingReadLog());
        services.AddBazaarReadAudit();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ReadAuditor>());
    }
}
