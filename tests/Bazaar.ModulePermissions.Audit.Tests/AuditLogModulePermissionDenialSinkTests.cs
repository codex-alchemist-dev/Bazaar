using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.ModulePermissions.Audit.Tests;

public class AuditLogModulePermissionDenialSinkTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 7, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An audit log that records entries, and can be told to fail or to block.</summary>
    private sealed class FakeAuditLog : IAuditLog
    {
        private readonly Lock _gate = new();
        private readonly List<AuditEntry> _entries = [];
        private int _entered;

        public int FailuresLeft { get; set; }

        public TaskCompletionSource? Blocker { get; set; }

        public int Attempts { get; private set; }

        /// <summary>Calls that have reached the log, including one that is still blocked.</summary>
        public int Entered => Volatile.Read(ref _entered);

        public IReadOnlyList<AuditEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public async Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _entered);
            if (Blocker is { } blocker)
            {
                await blocker.Task;
            }

            lock (_gate)
            {
                Attempts++;
                if (FailuresLeft > 0)
                {
                    FailuresLeft--;
                    throw new IOException("audit store is down");
                }

                _entries.Add(e);
            }
        }
    }

    private static ModulePermissionDenial Denial(string target = "owner/recovery-key", long suppressed = 0) =>
        new(_t0, "bazaar.weather", "secret", target, target.Length, ModuleAccessDecision.DeniedNotGranted, suppressed);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition(), "The sink did not reach the expected state in time.");
    }

    [Fact]
    public async Task A_refusal_becomes_one_audit_entry_with_a_fixed_payload()
    {
        var log = new FakeAuditLog();
        await using var sink = new AuditLogModulePermissionDenialSink(log, new FakeTimeProvider(_t0));

        sink.Record(Denial(suppressed: 3));
        await WaitForAsync(() => log.Entries.Count == 1);

        var entry = log.Entries[0];
        Assert.Equal(_t0, entry.Ts);
        Assert.Equal("module:bazaar.weather", entry.Actor);
        Assert.Equal("module.permission.denied", entry.Kind);
        using var payload = JsonDocument.Parse(entry.Payload);
        Assert.Equal(
            ["class", "decision", "module_id", "not_recorded_before", "target", "target_length"],
            payload.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("secret", payload.RootElement.GetProperty("class").GetString());
        Assert.Equal("not_granted", payload.RootElement.GetProperty("decision").GetString());
        Assert.Equal("owner/recovery-key", payload.RootElement.GetProperty("target").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("not_recorded_before").GetInt64());
    }

    [Fact]
    public async Task The_gate_and_the_sink_together_never_write_a_malformed_target()
    {
        var log = new FakeAuditLog();
        await using var sink = new AuditLogModulePermissionDenialSink(log, new FakeTimeProvider(_t0));
        var gate = new ModulePermissionGate(sink, new FakeTimeProvider(_t0));
        gate.Activate("bazaar.weather", ["secret:weather/*"]);

        gate.Check("bazaar.weather", new("secret", "weather/x\n\",\"kind\":\"module.permissions.approved"));
        await WaitForAsync(() => log.Entries.Count == 1);

        using var payload = JsonDocument.Parse(log.Entries[0].Payload);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("target").ValueKind);
        Assert.Equal("invalid_target", payload.RootElement.GetProperty("decision").GetString());
        Assert.DoesNotContain("approved", log.Entries[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_are_written_in_the_order_the_refusals_happened()
    {
        var log = new FakeAuditLog();
        await using var sink = new AuditLogModulePermissionDenialSink(log, new FakeTimeProvider(_t0));

        for (var i = 0; i < 50; i++)
        {
            sink.Record(Denial($"guess/{i}"));
        }

        await WaitForAsync(() => log.Entries.Count == 50);
        Assert.Equal(
            Enumerable.Range(0, 50).Select(i => $"guess/{i}"),
            log.Entries.Select(e => JsonElement.Parse(e.Payload).GetProperty("target").GetString()));
    }

    [Fact]
    public async Task A_failed_append_is_retried_and_not_skipped()
    {
        var log = new FakeAuditLog { FailuresLeft = 2 };
        var time = new FakeTimeProvider(_t0);
        await using var sink = new AuditLogModulePermissionDenialSink(log, time);

        sink.Record(Denial());
        for (var i = 0; i < 400 && log.Entries.Count == 0; i++)
        {
            time.Advance(AuditLogModulePermissionDenialSink.MaxRetryDelay);
            await Task.Delay(10, Ct);
        }

        Assert.Single(log.Entries);
        Assert.Equal(3, log.Attempts);
        Assert.Equal(1, sink.Written);
    }

    [Fact]
    public async Task When_the_audit_store_stalls_the_caller_is_never_blocked_and_the_overflow_is_counted_then_reported()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new FakeAuditLog { Blocker = blocker };
        await using var sink = new AuditLogModulePermissionDenialSink(log, new FakeTimeProvider(_t0));

        // The pump takes the first entry and stalls in the store. Only then is the queue filled, so the count of
        // refusals that do not fit is exact and the pump cannot report part of it early.
        sink.Record(Denial("guess/first"));
        await WaitForAsync(() => log.Entered == 1);

        const int overflow = 500;
        for (var i = 0; i < AuditLogModulePermissionDenialSink.QueueCapacity + overflow; i++)
        {
            sink.Record(Denial($"guess/{i}"));
        }

        Assert.Equal(overflow, sink.PendingOverflow);

        log.Blocker = null;
        blocker.SetResult();
        await WaitForAsync(() => log.Entries.Count == AuditLogModulePermissionDenialSink.QueueCapacity + 1);

        var reported = log.Entries.Sum(e => JsonElement.Parse(e.Payload).GetProperty("not_recorded_before").GetInt64());
        Assert.Equal(overflow, reported);
        Assert.Equal(0, sink.PendingOverflow);
    }

    [Fact]
    public async Task Disposing_writes_what_is_queued_and_then_takes_nothing_more()
    {
        var log = new FakeAuditLog();
        var sink = new AuditLogModulePermissionDenialSink(log, new FakeTimeProvider(_t0));
        for (var i = 0; i < 20; i++)
        {
            sink.Record(Denial($"guess/{i}"));
        }

        await sink.DisposeAsync();
        await sink.DisposeAsync();

        Assert.Equal(20, log.Entries.Count);
        sink.Record(Denial("late"));
        Assert.Equal(20, log.Entries.Count);
        Assert.Equal(1, sink.PendingOverflow);
    }

    [Fact]
    public async Task Disposing_while_the_store_is_down_ends_and_keeps_the_lost_count()
    {
        var log = new FakeAuditLog { FailuresLeft = int.MaxValue };
        var sink = new AuditLogModulePermissionDenialSink(log, new FakeTimeProvider(_t0));
        sink.Record(Denial("a"));
        sink.Record(Denial("b"));
        await WaitForAsync(() => log.Attempts >= 1);

        await sink.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(log.Entries);
        Assert.Equal(2, sink.PendingOverflow);
    }

    [Fact]
    public async Task The_container_wires_the_gate_to_the_audit_log()
    {
        var log = new FakeAuditLog();
        await using var services = new ServiceCollection()
            .AddSingleton<IAuditLog>(log)
            .AddBazaarModulePermissionAudit()
            .AddBazaarModulePermissions()
            .BuildServiceProvider();
        services.GetRequiredService<IModulePermissionRegistry>().Activate("bazaar.weather", ["gc.post"]);

        services.GetRequiredService<IModulePermissionGate>().Check("bazaar.weather", new("process", "sh"));

        await WaitForAsync(() => log.Entries.Count == 1);
    }
}
