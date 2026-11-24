namespace Bazaar.Server.DevSandbox.Tests;

public class SandboxReplayTests
{
    private static SandboxRunner Runner() =>
        new([new FakeProvider(SandboxOs.Linux)], [new ScriptedEngine([])], new FakeCompanyFactory(), null, SandboxOs.Linux);

    private static IReadOnlyList<SandboxAuditEvent> Record(params (SandboxAction A, SandboxDecision D, string R)[] items)
    {
        var t = new HashChainedAuditTrail();
        foreach (var (a, d, r) in items)
        {
            t.Append(a, d, r);
        }

        return t.Events;
    }

    [Fact]
    public async Task Replay_of_a_real_run_gives_same_verdict()
    {
        var runner = new SandboxRunner([new FakeProvider(SandboxOs.Linux)], [new ScriptedEngine([Specs.GoodFile, Specs.BadNet])], new FakeCompanyFactory(), null, SandboxOs.Linux);
        var spec = Specs.Valid();
        HashChainedAuditTrail? captured = null;
        var runner2 = new SandboxRunner(
            [new FakeProvider(SandboxOs.Linux)], [new ScriptedEngine([Specs.GoodFile, Specs.BadNet])], new FakeCompanyFactory(), () => captured = new HashChainedAuditTrail(), SandboxOs.Linux);
        var run = await runner2.RunAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.UndeclaredAccess, run.Verdict);
        var replay = runner.Replay(spec, captured!.Events);
        Assert.Equal(run.Verdict, replay.Verdict);
        Assert.Equal(run.AuditHeadHash, replay.AuditHeadHash);
    }

    [Fact]
    public void Clean_stream_replays_clean()
    {
        var r = Runner().Replay(Specs.Valid(), Record((Specs.GoodFile, SandboxDecision.Allowed, "granted")));
        Assert.Equal(SandboxVerdict.Clean, r.Verdict);
    }

    [Fact]
    public void Invalid_spec_is_refused()
    {
        var r = Runner().Replay(Specs.Valid("prod-1"), Record());
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
    }

    [Fact]
    public void Tampered_chain_is_error()
    {
        var events = Record((Specs.GoodFile, SandboxDecision.Allowed, "granted"), (Specs.BadFile, SandboxDecision.Denied, "no-grant")).ToList();
        events[1] = events[1] with { Decision = SandboxDecision.Allowed };
        var r = Runner().Replay(Specs.Valid(), events);
        Assert.Equal(SandboxVerdict.Error, r.Verdict);
        Assert.Equal("audit chain broken", r.Reason);
    }

    [Fact]
    public void Flipped_decision_with_valid_chain_is_mismatch_naming_sequence()
    {
        var events = Record((Specs.GoodFile, SandboxDecision.Allowed, "granted"), (Specs.BadFile, SandboxDecision.Allowed, "granted"));
        var r = Runner().Replay(Specs.Valid(), events);
        Assert.Equal(SandboxVerdict.ReplayMismatch, r.Verdict);
        Assert.Contains("sequence 1", r.Reason);
    }

    [Fact]
    public void Canary_event_replays_as_canary_triggered()
    {
        var leak = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "x");
        var r = Runner().Replay(Specs.Valid(), Record((Specs.BadFile, SandboxDecision.Denied, "no-grant"), (leak, SandboxDecision.Denied, "canary")));
        Assert.Equal(SandboxVerdict.CanaryTriggered, r.Verdict);
        Assert.Equal(2, r.Violations.Count);
    }

    [Fact]
    public void Canary_event_recorded_as_allowed_is_mismatch()
    {
        var leak = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "x");
        var r = Runner().Replay(Specs.Valid(), Record((leak, SandboxDecision.Allowed, "canary")));
        Assert.Equal(SandboxVerdict.ReplayMismatch, r.Verdict);
    }

    [Fact]
    public void Allowed_db_event_on_non_sandbox_schema_is_mismatch()
    {
        var db = new SandboxAction(SandboxActionKind.HostCall, "db.query", "db:public");
        var spec = Specs.Valid() with { Grants = [new SandboxPermissionGrant("db.query", "*")] };
        var r = Runner().Replay(spec, Record((db, SandboxDecision.Allowed, "granted")));
        Assert.Equal(SandboxVerdict.ReplayMismatch, r.Verdict);
    }
}
