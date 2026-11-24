using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.DevSandbox.Tests;

public class AuditLogSandboxResultSinkTests
{
    private sealed class CaptureLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Writes_one_entry_without_payloads_or_canaries()
    {
        var log = new CaptureLog();
        var violation = new SandboxAuditEvent(
            0, DateTimeOffset.UnixEpoch, new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "x", "CANARY-SECRET"), SandboxDecision.Denied, "canary", new string('0', 64), new string('a', 64));
        var result = new SandboxRunResult("run-1", SandboxVerdict.CanaryTriggered, "reason", [violation], 3, new string('b', 64));

        var before = DateTimeOffset.UtcNow;
        await new AuditLogSandboxResultSink(log).WriteAsync(result, "agent:charles", TestContext.Current.CancellationToken);

        var e = Assert.Single(log.Entries);
        Assert.Equal("sandbox.run", e.Kind);
        Assert.Equal("agent:charles", e.Actor);
        Assert.True(e.Ts >= before.AddSeconds(-1));
        Assert.DoesNotContain("CANARY-SECRET", e.Payload);
        using var doc = JsonDocument.Parse(e.Payload);
        var root = doc.RootElement;
        Assert.Equal("run-1", root.GetProperty("runId").GetString());
        Assert.Equal("CanaryTriggered", root.GetProperty("verdict").GetString());
        Assert.Equal(3, root.GetProperty("eventCount").GetInt64());
        Assert.Equal(new string('b', 64), root.GetProperty("auditHeadHash").GetString());
        Assert.Equal(1, root.GetProperty("violationCount").GetInt32());
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Rejects_empty_actor()
    {
        var sink = new AuditLogSandboxResultSink(new CaptureLog());
        var result = new SandboxRunResult("r", SandboxVerdict.Clean, "", [], 0, new string('0', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => sink.WriteAsync(result, "", TestContext.Current.CancellationToken));
    }
}
