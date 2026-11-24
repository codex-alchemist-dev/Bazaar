namespace Bazaar.Server.DevSandbox.Tests;

public class SandboxRunnerTests
{
    private static SandboxRunner Make(
        FakeProvider? provider,
        ISandboxEngine? engine,
        SandboxOs? os = SandboxOs.Linux,
        Action<SandboxRunResult>? onResult = null) =>
        new(
            provider is null ? [] : [provider],
            engine is null ? [] : [engine],
            new FakeCompanyFactory(),
            null,
            os,
            onResult);

    [Fact]
    public async Task Clean_run_has_clean_verdict_and_verified_chain()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var r = await Make(p, new ScriptedEngine([Specs.GoodFile, Specs.GoodNet])).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Clean, r.Verdict);
        Assert.Empty(r.Violations);
        Assert.Equal(2, r.EventCount);
        Assert.Equal(64, r.AuditHeadHash.Length);
        Assert.Equal(1, p.Last!.Kills);
        Assert.Equal(1, p.Last.Disposals);
    }

    [Fact]
    public async Task Undeclared_file_access_fails_and_kills()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var r = await Make(p, new ScriptedEngine([Specs.GoodFile, Specs.BadFile, Specs.GoodFile])).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
        Assert.Single(r.Violations);
        Assert.Equal(SandboxDecision.Denied, r.Violations[0].Decision);
        Assert.True(p.Last!.Kills >= 1);
        Assert.Equal(1, p.Last.Disposals);
    }

    [Fact]
    public async Task Network_to_non_allowlisted_host_is_denied()
    {
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([Specs.BadNet])).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
    }

    [Fact]
    public async Task Canary_in_payload_triggers_and_does_not_leak_into_reason()
    {
        var leak = new SandboxAction(SandboxActionKind.HostCall, "host.call", "log", "value " + FakeCompanyFactory.Canary);
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([leak, Specs.GoodFile])).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.CanaryTriggered, r.Verdict);
        Assert.DoesNotContain(FakeCompanyFactory.Canary, r.Reason);
        Assert.Equal(1, r.EventCount);
    }

    [Fact]
    public async Task Canary_in_network_payload_triggers()
    {
        var leak = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "x=" + FakeCompanyFactory.Canary);
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([leak])).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.CanaryTriggered, r.Verdict);
    }

    [Fact]
    public async Task Canary_outranks_an_earlier_violation()
    {
        var leak = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", FakeCompanyFactory.Canary);
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([Specs.BadFile, leak])).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.CanaryTriggered, r.Verdict);
        Assert.Equal(2, r.Violations.Count);
    }

    [Fact]
    public async Task Untested_provider_refuses_and_engine_never_runs()
    {
        var e = new ScriptedEngine([Specs.GoodFile]);
        var p = new FakeProvider(SandboxOs.Linux, tested: false);
        var r = await Make(p, e).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, e.Runs);
        Assert.Equal(0, p.Starts);
    }

    [Fact]
    public async Task No_provider_for_host_os_refuses()
    {
        var e = new ScriptedEngine([Specs.GoodFile]);
        var r = await Make(new FakeProvider(SandboxOs.Windows), e).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, e.Runs);
    }

    [Fact]
    public async Task No_engine_refuses()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var r = await Make(p, null).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, p.Starts);
    }

    [Fact]
    public async Task Invalid_spec_refuses_without_starting_provider()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var bad = Specs.Valid() with { RunId = "Bad Run!" };
        var r = await Make(p, new ScriptedEngine([])).RunAsync(bad, TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, p.Starts);
    }

    [Fact]
    public async Task Production_target_refuses()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var r = await Make(p, new ScriptedEngine([])).RunAsync(Specs.Valid("prod-1"), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, p.Starts);
    }

    [Fact]
    public async Task Non_sandbox_company_schema_refuses()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var runner = new SandboxRunner([p], [new ScriptedEngine([])], new FakeCompanyFactory { Schema = "public" }, null, SandboxOs.Linux);
        var r = await runner.RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, p.Starts);
    }

    [Theory]
    [InlineData("db:public")]
    [InlineData("db:public.users")]
    [InlineData("db:sandbox_other")]
    public async Task Db_action_to_non_sandbox_schema_is_denied(string target)
    {
        var db = new SandboxAction(SandboxActionKind.HostCall, "db.query", target);
        var spec = Specs.Valid() with { Grants = [new SandboxPermissionGrant("db.query", "*")] };
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([db])).RunAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
        Assert.Equal("production-target", r.Violations[0].Reason);
    }

    [Fact]
    public async Task Timeout_yields_timed_out_and_kills()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var r = await Make(p, new ScriptedEngine([Specs.GoodFile], neverEnd: true)).RunAsync(Specs.Valid(timeout: 1), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.TimedOut, r.Verdict);
        Assert.True(p.Last!.Kills >= 1);
        Assert.Equal(1, p.Last.Disposals);
    }

    [Fact]
    public async Task Engine_exception_is_error_without_message()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        var secret = new InvalidOperationException("secret " + FakeCompanyFactory.Canary);
        var r = await Make(p, new ScriptedEngine([Specs.GoodFile], secret)).RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Error, r.Verdict);
        Assert.Contains(nameof(InvalidOperationException), r.Reason);
        Assert.DoesNotContain("secret", r.Reason);
        Assert.DoesNotContain(FakeCompanyFactory.Canary, r.Reason);
        Assert.Equal(1, p.Last!.Disposals);
        Assert.True(p.Last.Kills >= 1);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_process_is_disposed()
    {
        var p = new FakeProvider(SandboxOs.Linux);
        using var cts = new CancellationTokenSource();
        var task = Make(p, new ScriptedEngine([], neverEnd: true)).RunAsync(Specs.Valid(), cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, p.Last!.Disposals);
    }

    [Fact]
    public async Task OnResult_receives_result_and_its_exceptions_are_swallowed()
    {
        SandboxRunResult? seen = null;
        var ok = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([Specs.GoodFile]), onResult: x => seen = x)
            .RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Same(ok, seen);

        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([Specs.GoodFile]), onResult: _ => throw new InvalidOperationException())
            .RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Clean, r.Verdict);
    }
}
