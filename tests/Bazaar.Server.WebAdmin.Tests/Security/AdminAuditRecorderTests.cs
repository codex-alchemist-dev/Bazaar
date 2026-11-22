using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin.Security;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.WebAdmin.Tests.Security;

public sealed class AdminAuditRecorderTests
{
    private sealed class FakeLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Exception? Fail { get; set; }

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private static (AdminAuditRecorder Rec, FakeLog Log, FakeTimeProvider Time) Make()
    {
        var log = new FakeLog();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        return (new AdminAuditRecorder(log, time), log, time);
    }

    [Fact]
    public async Task Records_entry_with_json_payload_and_time()
    {
        var (rec, log, time) = Make();
        await rec.RecordAsync(AdminAuditKinds.SelectionCommit, "human:alice",
            new Dictionary<string, string> { ["revision"] = "7", ["note"] = "quote \" and \\ ok" }, ct: TestContext.Current.CancellationToken);

        var e = Assert.Single(log.Entries);
        Assert.Equal("human:alice", e.Actor);
        Assert.Equal(AdminAuditKinds.SelectionCommit, e.Kind);
        Assert.Equal(time.GetUtcNow(), e.Ts);
        using var doc = JsonDocument.Parse(e.Payload);
        Assert.Equal("7", doc.RootElement.GetProperty("revision").GetString());
        Assert.Equal("quote \" and \\ ok", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Empty_fields_give_empty_object()
    {
        var (rec, log, _) = Make();
        await rec.RecordAsync(AdminAuditKinds.SignIn, "human:a", new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);
        Assert.Equal("{}", Assert.Single(log.Entries).Payload);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("sessionToken")]
    [InlineData("client_SECRET")]
    [InlineData("Cookie")]
    [InlineData("passwordHash")]
    public async Task Secret_looking_keys_are_refused(string key)
    {
        var (rec, log, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            rec.RecordAsync(AdminAuditKinds.Read, "human:a", new Dictionary<string, string> { [key] = "x" }, ct: TestContext.Current.CancellationToken));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Long_values_are_refused_but_limit_is_allowed()
    {
        var (rec, log, _) = Make();
        await rec.RecordAsync(AdminAuditKinds.Read, "human:a", new Dictionary<string, string> { ["v"] = new string('a', 512) }, ct: TestContext.Current.CancellationToken);
        Assert.Single(log.Entries);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            rec.RecordAsync(AdminAuditKinds.Read, "human:a", new Dictionary<string, string> { ["v"] = new string('a', 513) }, ct: TestContext.Current.CancellationToken));
        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task Append_failure_propagates()
    {
        var (rec, log, _) = Make();
        log.Fail = new InvalidOperationException("chain down");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rec.RecordAsync(AdminAuditKinds.RebuildStart, "human:a", new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken));
        Assert.Equal("chain down", ex.Message);
    }
}
