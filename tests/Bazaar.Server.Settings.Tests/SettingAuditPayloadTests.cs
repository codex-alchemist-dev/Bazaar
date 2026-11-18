using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Settings.Tests;

/// <summary>
/// The audit entry a settings write produces, checked without a database: the kind per outcome, the actor and the
/// payload fields. The chain itself is the audit log's job and is tested with the Postgres audit tests.
/// </summary>
public sealed class SettingAuditPayloadTests
{
    [Theory]
    [InlineData(SettingWriteOutcome.Accepted, "setting.changed", "accepted")]
    [InlineData(SettingWriteOutcome.PendingApproval, "setting.held", "pending_approval")]
    [InlineData(SettingWriteOutcome.Rejected, "setting.rejected", "rejected")]
    public async Task Each_outcome_writes_one_entry_with_its_kind_and_wire_outcome(SettingWriteOutcome outcome, string kind, string wire)
    {
        var log = new RecordingAuditLog();
        var sink = new PostgresSettingAuditSink(log);
        var reason = outcome == SettingWriteOutcome.Accepted ? null : "needs approval";

        await sink.AppendAsync(new SettingChangeAudit(
            "host.status.port", SettingScope.Install, "", "operator:dom", outcome, "8080", "9090", reason), TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(kind, entry.Kind);
        Assert.Equal("operator:dom", entry.Actor);

        var payload = JsonElement.Parse(entry.Payload);
        Assert.Equal("host.status.port", payload.GetProperty("key").GetString());
        Assert.Equal("install", payload.GetProperty("scope").GetString());
        Assert.Equal("", payload.GetProperty("scope_id").GetString());
        Assert.Equal(wire, payload.GetProperty("outcome").GetString());
        Assert.Equal(8080, payload.GetProperty("old").GetInt32());
        Assert.Equal(9090, payload.GetProperty("new").GetInt32());
        Assert.Equal(reason, payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_value_that_is_not_json_is_kept_as_a_json_string()
    {
        var log = new RecordingAuditLog();
        var sink = new PostgresSettingAuditSink(log);

        await sink.AppendAsync(new SettingChangeAudit(
            "host.status.port", SettingScope.Install, "", "operator:dom", SettingWriteOutcome.Rejected, null, "not-json", "bad"), TestContext.Current.CancellationToken);

        var payload = JsonElement.Parse(Assert.Single(log.Entries).Payload);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("old").ValueKind);
        Assert.Equal("not-json", payload.GetProperty("new").GetString());
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }
}
