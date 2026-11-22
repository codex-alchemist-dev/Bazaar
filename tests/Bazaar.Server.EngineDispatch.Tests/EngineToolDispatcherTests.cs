using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.EngineDispatch.Tests;

public sealed class EngineToolDispatcherTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class Grants : IEngineGrantReader
    {
        public bool Covers = true;
        public Exception? Throw;
        public bool Hang;
        public int Calls;

        public async ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct)
        {
            Calls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Covers;
        }
    }

    private sealed class Taint : ISessionTaintReader
    {
        public bool Hard;
        public Exception? Throw;

        public ValueTask<bool> IsHardTaintedAsync(Guid agentId, Guid? sessionId, CancellationToken ct) =>
            Throw is not null ? throw Throw : ValueTask.FromResult(Hard);
    }

    private sealed class Recorder : IEngineGcRecorder
    {
        public List<EngineToolCallRecord> Records = [];
        public Exception? Throw;

        public Task RecordAsync(EngineToolCallRecord record, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Audit : IAuditLog
    {
        public List<AuditEntry> Entries = [];
        public Exception? Throw;

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public Grants Grants = new();
        public Taint Taint = new();
        public Recorder Recorder = new();
        public Audit Audit = new();

        public EngineToolDispatcher Build(EngineDispatchOptions? o = null) =>
            new(Grants, Taint, Recorder, Audit, o ?? new EngineDispatchOptions { RobloxExecOwnerApproval = false });
    }

    private static EngineDispatchRequest Req(string tool, string args = "{}", string? grant = "g1", string adapter = "roblox", bool approved = false) =>
        new(_agent, adapter, grant, "studio-1", tool, JsonElement.Parse(args)) { OwnerApprovalVerified = approved };

    private static EngineDispatchOptions Hosts(params string[] hosts) =>
        new() { RobloxExecOwnerApproval = false, HttpAllowlist = hosts };

    [Fact]
    public async Task Unknown_tool_is_denied_and_never_asks_the_grant_reader()
    {
        var rig = new Rig();
        var d = await rig.Build().AuthorizeAsync(Req("format_disk"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.UnknownTool, d.Reason);
        Assert.False(d.Allowed);
        Assert.Equal(0, rig.Grants.Calls);
    }

    [Fact]
    public async Task Read_tool_is_allowed_without_grant_and_not_audited()
    {
        var rig = new Rig();
        var d = await rig.Build().AuthorizeAsync(Req("script_read", grant: null), TestContext.Current.CancellationToken);
        Assert.True(d.Allowed);
        Assert.Empty(rig.Audit.Entries);
        Assert.Empty(rig.Recorder.Records);
    }

    [Fact]
    public async Task Write_without_grant_is_denied_and_audited()
    {
        var rig = new Rig();
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit", grant: null), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.GrantMissing, d.Reason);
        var e = Assert.Single(rig.Audit.Entries);
        Assert.Equal("engine.tool.call", e.Kind);
        Assert.Equal(_agent.ToString(), e.Actor);
        Assert.Contains("\"decision\":\"denied\"", e.Payload);
        Assert.False(Assert.Single(rig.Recorder.Records).Allowed);
    }

    [Fact]
    public async Task Grant_not_covering_is_denied_and_audited()
    {
        var rig = new Rig { Grants = { Covers = false } };
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.GrantNotCovering, d.Reason);
        Assert.Single(rig.Audit.Entries);
    }

    [Fact]
    public async Task Covered_write_is_allowed_and_audited_and_recorded()
    {
        var rig = new Rig();
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.True(d.Allowed);
        Assert.Equal(RiskClass.Write, d.RiskClass);
        Assert.Contains("\"decision\":\"allowed\"", Assert.Single(rig.Audit.Entries).Payload);
        Assert.True(Assert.Single(rig.Recorder.Records).Allowed);
    }

    [Fact]
    public async Task Grant_reader_exception_fails_closed()
    {
        var rig = new Rig { Grants = { Throw = new InvalidOperationException("db down") } };
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.ReaderFailure, d.Reason);
        Assert.Single(rig.Audit.Entries);
    }

    [Fact]
    public async Task Grant_reader_timeout_fails_closed()
    {
        var rig = new Rig { Grants = { Hang = true } };
        var o = new EngineDispatchOptions { ReaderTimeout = TimeSpan.FromMilliseconds(50) };
        var d = await rig.Build(o).AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.ReaderFailure, d.Reason);
    }

    [Fact]
    public async Task Taint_reader_exception_fails_closed_for_exec()
    {
        var rig = new Rig { Taint = { Throw = new InvalidOperationException() } };
        var d = await rig.Build().AuthorizeAsync(Req("start_stop_play"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.ReaderFailure, d.Reason);
    }

    [Fact]
    public async Task Audit_failure_denies_and_does_not_record()
    {
        var rig = new Rig { Audit = { Throw = new IOException() } };
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.AuditFailure, d.Reason);
        Assert.False(d.Allowed);
        Assert.Empty(rig.Recorder.Records);
    }

    [Fact]
    public async Task Recorder_failure_denies_and_leaves_a_correcting_audit_row()
    {
        var rig = new Rig { Recorder = { Throw = new IOException() } };
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.RecorderFailure, d.Reason);
        Assert.Equal(2, rig.Audit.Entries.Count);
        Assert.Contains("\"decision\":\"denied\"", rig.Audit.Entries[1].Payload);
    }

    [Fact]
    public async Task Hard_tainted_exec_is_denied_even_when_owner_approval_setting_is_off()
    {
        var rig = new Rig { Taint = { Hard = true } };
        var d = await rig.Build().AuthorizeAsync(Req("start_stop_play"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.HardTaintNeedsApproval, d.Reason);
        Assert.Single(rig.Audit.Entries);
    }

    [Fact]
    public async Task Hard_tainted_exec_with_verified_approval_is_allowed()
    {
        var rig = new Rig { Taint = { Hard = true } };
        var d = await rig.Build().AuthorizeAsync(Req("start_stop_play", approved: true), TestContext.Current.CancellationToken);
        Assert.True(d.Allowed);
    }

    [Fact]
    public async Task Hard_tainted_write_is_not_blocked_by_the_exec_rule()
    {
        var rig = new Rig { Taint = { Hard = true } };
        var d = await rig.Build().AuthorizeAsync(Req("multi_edit"), TestContext.Current.CancellationToken);
        Assert.True(d.Allowed);
    }

    [Fact]
    public async Task Roblox_exec_needs_owner_approval_by_default()
    {
        var rig = new Rig();
        var d = await rig.Build(new EngineDispatchOptions()).AuthorizeAsync(Req("start_stop_play"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.OwnerApprovalRequired, d.Reason);
        var ok = await rig.Build(new EngineDispatchOptions()).AuthorizeAsync(Req("start_stop_play", approved: true), TestContext.Current.CancellationToken);
        Assert.True(ok.Allowed);
    }

    [Fact]
    public async Task Http_get_is_denied_by_default_allowlist()
    {
        var rig = new Rig();
        var d = await rig.Build().AuthorizeAsync(Req("http_get", "{\"url\":\"https://example.com/x\"}"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.HostNotAllowlisted, d.Reason);
    }

    [Fact]
    public async Task Http_get_without_url_is_denied()
    {
        var rig = new Rig();
        var d = await rig.Build(Hosts("example.com")).AuthorizeAsync(Req("http_get"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineDispatchReason.HostNotAllowlisted, d.Reason);
    }

    [Theory]
    [InlineData("https://example.com/path?q=1", true)]
    [InlineData("HTTPS://EXAMPLE.COM/", true)]
    [InlineData("https://example.com./", true)]
    [InlineData("https://example.com:443/", true)]
    [InlineData("https://evil-example.com/", false)]
    [InlineData("https://example.com.evil.com/", false)]
    [InlineData("https://sub.example.com/", false)]
    [InlineData("https://notexample.com/", false)]
    [InlineData("https://example.com@evil.com/", false)]
    [InlineData("https://user:pw@example.com/", false)]
    [InlineData("https://example.com:8443/", false)]
    [InlineData("https://evil.com/?h=example.com", false)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("example.com", false)]
    public async Task Http_get_host_match_is_exact(string url, bool allowed)
    {
        var rig = new Rig();
        var args = JsonSerializer.Serialize(new { url });
        var d = await rig.Build(Hosts("example.com")).AuthorizeAsync(Req("http_get", args), TestContext.Current.CancellationToken);
        Assert.Equal(allowed, d.Allowed);
    }

    [Fact]
    public async Task Execute_luau_with_unlisted_url_is_denied_and_listed_url_is_allowed()
    {
        var rig = new Rig();
        var bad = JsonSerializer.Serialize(new { code = "game:GetService('HttpService'):GetAsync('https://evil.com/x')" });
        var good = JsonSerializer.Serialize(new { code = "HttpService:GetAsync(\"https://example.com/x\")" });
        var none = JsonSerializer.Serialize(new { code = "print(1)" });
        var d = rig.Build(Hosts("example.com"));
        Assert.Equal(EngineDispatchReason.HostNotAllowlisted, (await d.AuthorizeAsync(Req("execute_luau", bad), TestContext.Current.CancellationToken)).Reason);
        Assert.True((await d.AuthorizeAsync(Req("execute_luau", good), TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await d.AuthorizeAsync(Req("execute_luau", none), TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task Audit_payload_has_a_hash_but_never_the_args_content()
    {
        var rig = new Rig();
        const string secret = "TOP-SECRET-TOKEN-123";
        await rig.Build().AuthorizeAsync(Req("multi_edit", $"{{\"source\":\"{secret}\"}}"), TestContext.Current.CancellationToken);
        var payload = Assert.Single(rig.Audit.Entries).Payload;
        Assert.DoesNotContain(secret, payload);
        using var doc = JsonDocument.Parse(payload);
        var hash = doc.RootElement.GetProperty("args_sha256").GetString();
        Assert.Equal(64, hash!.Length);
        Assert.Equal("multi_edit", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("write", doc.RootElement.GetProperty("risk_class").GetString());
        Assert.Equal("g1", doc.RootElement.GetProperty("grant_id").GetString());
        Assert.DoesNotContain(secret, rig.Recorder.Records[0].ToString());
    }

    [Fact]
    public async Task Cancelled_token_propagates()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Build().AuthorizeAsync(Req("multi_edit"), cts.Token));
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task Caller_cancellation_during_grant_read_is_not_swallowed_as_a_denial()
    {
        var rig = new Rig { Grants = { Hang = true } };
        using var cts = new CancellationTokenSource();
        var task = rig.Build().AuthorizeAsync(Req("multi_edit"), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task VerifyGrant_covers_and_fails_closed()
    {
        var rig = new Rig();
        var d = rig.Build();
        Assert.True(await d.VerifyGrantAsync(_agent, "roblox", "g1", "multi_edit", RiskClass.Write, TestContext.Current.CancellationToken));
        Assert.False(await d.VerifyGrantAsync(_agent, "roblox", "g1", "multi_edit", RiskClass.Exec, TestContext.Current.CancellationToken));
        Assert.False(await d.VerifyGrantAsync(_agent, "roblox", "g1", "nope", RiskClass.Write, TestContext.Current.CancellationToken));
        Assert.False(await d.VerifyGrantAsync(_agent, "roblox", "", "multi_edit", RiskClass.Write, TestContext.Current.CancellationToken));
        rig.Grants.Throw = new IOException();
        Assert.False(await d.VerifyGrantAsync(_agent, "roblox", "g1", "multi_edit", RiskClass.Write, TestContext.Current.CancellationToken));
    }
}
