using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>Records every audit entry in arrival order.</summary>
internal sealed class RecordingAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries => _entries;

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        _entries.Add(e);
        return Task.CompletedTask;
    }
}

public class KillSwitchAuditAdapterTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(_start);
    private readonly RecordingAuditLog _log = new();
    private readonly RecordingContainment _containment = new();
    private readonly KillSwitchEngine _engine;

    public KillSwitchAuditAdapterTests()
    {
        _engine = new KillSwitchEngine(new KillSwitchAuditAdapter(_log), _containment, _time);
    }

    [Fact]
    public async Task Engaging_a_global_stop_writes_exactly_one_record()
    {
        await _engine.EngageGlobalAsync("owner-1", "Suspicious traffic", TestContext.Current.CancellationToken);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(KillSwitchAuditAdapter.EngagedKind, entry.Kind);
        Assert.Equal("owner-1", entry.Actor);
        Assert.Equal(_start, entry.Ts);

        var payload = JsonNode.Parse(entry.Payload)!.AsObject();
        Assert.Equal("engaged", payload["action"]!.GetValue<string>());
        Assert.Equal("global", payload["scope"]!.GetValue<string>());
        Assert.Null(payload["agent_id"]);
        Assert.Equal("Suspicious traffic", payload["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Resuming_a_global_stop_writes_exactly_one_resumed_record()
    {
        await _engine.EngageGlobalAsync("owner-1", "Suspicious traffic", TestContext.Current.CancellationToken);
        await _engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken);

        Assert.Equal(2, _log.Entries.Count);
        var resumed = _log.Entries[1];
        Assert.Equal(KillSwitchAuditAdapter.ResumedKind, resumed.Kind);
        Assert.Equal("owner-1", resumed.Actor);
        Assert.Equal("resumed", JsonNode.Parse(resumed.Payload)!["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task Engaging_and_resuming_an_agent_stop_writes_one_record_each_with_the_agent_id()
    {
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Budget breach", TestContext.Current.CancellationToken);
        await _engine.ResumeAgentAsync("owner-1", "agent-a", "Reviewed", TestContext.Current.CancellationToken);

        Assert.Equal(2, _log.Entries.Count);
        foreach (var entry in _log.Entries)
        {
            var payload = JsonNode.Parse(entry.Payload)!.AsObject();
            Assert.Equal("agent", payload["scope"]!.GetValue<string>());
            Assert.Equal("agent-a", payload["agent_id"]!.GetValue<string>());
        }

        Assert.Equal(KillSwitchAuditAdapter.EngagedKind, _log.Entries[0].Kind);
        Assert.Equal(KillSwitchAuditAdapter.ResumedKind, _log.Entries[1].Kind);
    }

    [Fact]
    public async Task A_breach_stop_is_recorded_under_the_breach_actor()
    {
        await _engine.ReportBreachAsync("agent-b", "Rate limit exceeded", TestContext.Current.CancellationToken);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(KillSwitchEngine.BreachActorId, entry.Actor);
        Assert.Equal(KillSwitchAuditAdapter.EngagedKind, entry.Kind);
    }

    [Fact]
    public async Task RecordAsync_appends_one_entry_for_the_given_record()
    {
        var adapter = new KillSwitchAuditAdapter(_log);
        var record = new KillSwitchAuditEntry(_start, "owner-2", KillSwitchAction.Resumed, KillSwitchScope.Global, null, "Done");

        await adapter.RecordAsync(record, TestContext.Current.CancellationToken);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(KillSwitchAuditAdapter.ResumedKind, entry.Kind);
        Assert.Equal("owner-2", entry.Actor);
    }

    [Fact]
    public async Task RecordAsync_rejects_a_null_entry()
    {
        var adapter = new KillSwitchAuditAdapter(_log);

        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.RecordAsync(null!, TestContext.Current.CancellationToken));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void The_constructor_rejects_a_null_audit_log()
    {
        Assert.Throws<ArgumentNullException>(() => new KillSwitchAuditAdapter(null!));
    }
}
