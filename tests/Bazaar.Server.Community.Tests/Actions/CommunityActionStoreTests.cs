using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Community.Actions;

namespace Bazaar.Server.Community.Tests.Actions;

public class CommunityActionStoreTests
{
    private const string _body = "secret body text";

    private static readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    // Appends in the list test: a1=1, a2=2, b1=3 (agent-2), a3=4 (reddit), a4=5.
    private static readonly long[] _newestFirstForAgentOne = [5L, 2L, 1L];
    private static readonly long[] _newestTwoForAgentOne = [5L, 2L];

    private readonly InMemoryCommunityActionSql _sql = new();
    private readonly FakeAuditLog _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CommunityActionStore Store() => new(_sql, _audit, new FixedClock(_now));

    private static CommunityActionRecord Post(string? body = _body, string agentId = "agent-1", string platform = "github") =>
        new(0, agentId, "sponsor-1", platform, CommunityAction.NewPost, "https://example.test/thread", body, null, null, "[]", null, "done", default);

    [Fact]
    public async Task Append_writes_one_row_and_one_audit_entry()
    {
        var id = await Store().AppendAsync(Post(), Ct);

        Assert.Equal(1L, id);
        Assert.Single(_sql.Rows);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(CommunityActionStore.AuditKind, entry.Kind);
        Assert.Equal("community.action", entry.Kind);
        Assert.Equal("agent-1", entry.Actor);
        Assert.Equal(_now, entry.Ts);
    }

    [Fact]
    public async Task Audit_payload_carries_id_action_platform_and_body_hash_but_no_body_text()
    {
        await Store().AppendAsync(Post(), Ct);

        var payload = Assert.Single(_audit.Entries).Payload;
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal(1L, root.GetProperty("id").GetInt64());
        Assert.Equal("NewPost", root.GetProperty("action").GetString());
        Assert.Equal("github", root.GetProperty("platform").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_body))), root.GetProperty("body_sha256").GetString());
        Assert.DoesNotContain(_body, payload, StringComparison.Ordinal);
        Assert.Equal(4, CountProperties(root));
    }

    [Fact]
    public async Task Append_computes_the_body_hash_stored_on_the_row()
    {
        await Store().AppendAsync(Post(), Ct);

        var row = Assert.Single(_sql.Rows);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(_body)), row.BodySha256);
    }

    [Fact]
    public async Task Append_without_body_audits_a_null_hash()
    {
        await Store().AppendAsync(Post(body: null), Ct);

        using var doc = JsonDocument.Parse(Assert.Single(_audit.Entries).Payload);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("body_sha256").ValueKind);
        Assert.Null(Assert.Single(_sql.Rows).BodySha256);
    }

    [Fact]
    public async Task Append_rejects_a_supplied_hash_that_does_not_match_the_body()
    {
        var record = Post() with { BodySha256 = new byte[32] };

        await Assert.ThrowsAsync<ArgumentException>(() => Store().AppendAsync(record, Ct));
        Assert.Empty(_sql.Rows);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Append_rejects_blank_identity_fields_and_unknown_actions()
    {
        var store = Store();

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(Post(agentId: " "), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(Post() with { Action = (CommunityAction)999 }, Ct));
        Assert.Empty(_sql.Rows);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Withdrawal_adds_a_row_and_leaves_the_original_row_as_it_was()
    {
        var store = Store();
        var original = await store.AppendAsync(Post(), Ct);
        var before = _sql.Rows[0];

        var withdrawal = await store.WithdrawAsync("agent-1", "sponsor-1", "github", "post-42", ct: Ct);

        Assert.Equal(2L, withdrawal);
        Assert.Equal(2, _sql.Rows.Count);
        Assert.Equal(original, _sql.Rows[0].Id);
        Assert.Equal(before, _sql.Rows[0]);

        var row = _sql.Rows[1];
        Assert.Equal(CommunityAction.WithdrawOwn, row.Action);
        Assert.Equal("post-42", row.TargetUrl);
        Assert.Null(row.Body);
        Assert.Equal("done", row.Status);

        Assert.Equal(2, _audit.Entries.Count);
        using var doc = JsonDocument.Parse(_audit.Entries[1].Payload);
        Assert.Equal("WithdrawOwn", doc.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Withdrawal_rejects_a_blank_external_id()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().WithdrawAsync("agent-1", "sponsor-1", "github", " ", ct: Ct));
        Assert.Empty(_sql.Rows);
    }

    [Fact]
    public async Task Taint_sources_and_approval_are_stored_as_given()
    {
        var record = Post() with { TaintSourcesJson = "[\"web:example.test\"]", ApprovalId = "approval-7" };

        await Store().AppendAsync(record, Ct);

        var row = Assert.Single(_sql.Rows);
        Assert.Equal("[\"web:example.test\"]", row.TaintSourcesJson);
        Assert.Equal("approval-7", row.ApprovalId);
    }

    [Fact]
    public async Task A_failed_audit_write_throws_after_the_row_is_committed()
    {
        _audit.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().AppendAsync(Post(), Ct));

        // The row exists and has no audit entry. The caller must treat the action as unconfirmed.
        Assert.Single(_sql.Rows);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task List_returns_only_the_agent_and_platform_newest_first_and_respects_the_limit()
    {
        var store = Store();
        await store.AppendAsync(Post(body: "a1"), Ct);
        await store.AppendAsync(Post(body: "a2"), Ct);
        await store.AppendAsync(Post(body: "b1", agentId: "agent-2"), Ct);
        await store.AppendAsync(Post(body: "a3", platform: "reddit"), Ct);
        await store.AppendAsync(Post(body: "a4"), Ct);

        var rows = await store.ListForAgentAsync("agent-1", "github", 10, Ct);

        Assert.Equal(_newestFirstForAgentOne, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r => Assert.Equal("agent-1", r.AgentId));

        var limited = await store.ListForAgentAsync("agent-1", "github", 2, Ct);
        Assert.Equal(_newestTwoForAgentOne, limited.Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CommunityActionStore.MaxListLimit + 1)]
    public async Task List_rejects_limits_outside_the_allowed_range(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store().ListForAgentAsync("agent-1", "github", limit, Ct));
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
            count++;
        return count;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
