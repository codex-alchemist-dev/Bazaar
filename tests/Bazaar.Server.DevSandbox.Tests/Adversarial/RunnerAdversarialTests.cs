using System.Runtime.CompilerServices;

namespace Bazaar.Server.DevSandbox.Tests.Adversarial;

public class RunnerAdversarialTests
{
    private static SandboxRunner Make(FakeProvider? provider, ISandboxEngine? engine, SandboxOs? os = SandboxOs.Linux, ISandboxCompanyFactory? company = null) =>
        new(provider is null ? [] : [provider], engine is null ? [] : [engine], company ?? new FakeCompanyFactory(), null, os);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Untested_provider_for_the_host_os_means_no_start_and_no_engine_run()
    {
        var provider = new FakeProvider(SandboxOs.Linux, tested: false);
        var engine = new ScriptedEngine([Specs.GoodFile]);

        var r = await Make(provider, engine).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, provider.Starts);
        Assert.Equal(0, engine.Runs);
    }

    [Fact]
    public async Task A_tested_provider_for_another_os_is_not_used()
    {
        var provider = new FakeProvider(SandboxOs.Windows);

        var r = await Make(provider, new ScriptedEngine([]), SandboxOs.Linux).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, provider.Starts);
    }

    [Fact]
    public async Task An_untested_provider_registered_before_a_tested_one_is_skipped_not_chosen()
    {
        var bad = new FakeProvider(SandboxOs.Linux, tested: false);
        var good = new FakeProvider(SandboxOs.Linux);
        var runner = new SandboxRunner([bad, good], [new ScriptedEngine([])], new FakeCompanyFactory(), null, SandboxOs.Linux);

        var r = await runner.RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.Clean, r.Verdict);
        Assert.Equal(0, bad.Starts);
        Assert.Equal(1, good.Starts);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("production")]
    [InlineData("Sandbox-x")]
    [InlineData("SANDBOX-x")]
    [InlineData(" sandbox-x")]
    [InlineData("sandbox-x ")]
    [InlineData("sandbox-x\n")]
    [InlineData("sandbox-x\0")]
    [InlineData("sandbox-")]
    [InlineData("sandbox-x;drop schema public")]
    [InlineData("sandbox-x.public")]
    [InlineData("sandbox-x/../prod")]
    [InlineData("sаndbox-x")]
    [InlineData("sandbox‐x")]
    [InlineData("sandbox－x")]
    [InlineData("sandbox-а")]
    [InlineData("public")]
    [InlineData("sandbox_x")]
    [InlineData("")]
    public async Task Production_or_lookalike_target_company_is_refused_before_anything_starts(string target)
    {
        var provider = new FakeProvider(SandboxOs.Linux);

        var r = await Make(provider, new ScriptedEngine([])).RunAsync(Specs.Valid(targetCompany: target), Ct);

        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, provider.Starts);
    }

    [Fact]
    public async Task A_target_company_id_longer_than_a_postgres_identifier_is_refused()
    {
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([]))
            .RunAsync(Specs.Valid(targetCompany: "sandbox-" + new string('a', 80)), Ct);

        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    [InlineData("Sandbox_run_1")]
    [InlineData("sandbox")]
    [InlineData("")]
    [InlineData("company_main")]
    public async Task A_company_factory_that_returns_a_non_sandbox_schema_is_refused(string schema)
    {
        var provider = new FakeProvider(SandboxOs.Linux);

        var r = await Make(provider, new ScriptedEngine([]), company: new FakeCompanyFactory { Schema = schema }).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(0, provider.Starts);
    }

    [Theory]
    [InlineData("db:public.users")]
    [InlineData("DB:public.users")]
    [InlineData("Db:public.users")]
    [InlineData(" db:public.users")]
    [InlineData("\tdb:public.users")]
    [InlineData("db:sandbox_run_1,public.users")]
    [InlineData("db:sandbox_run_10.users")]
    [InlineData("db:sandbox_run_2.users")]
    [InlineData("db:SANDBOX_run_1.users")]
    [InlineData("db:\"public\".users")]
    [InlineData("db:pg_catalog.pg_authid")]
    public async Task Any_kind_of_action_aimed_at_a_foreign_schema_is_denied_as_production_target(string target)
    {
        var spec = Specs.Valid() with { Grants = [new SandboxPermissionGrant("db.query", "*")] };
        foreach (var kind in new[] { SandboxActionKind.HostCall, SandboxActionKind.FileAccess, SandboxActionKind.SecretRead, SandboxActionKind.PermissionCheck })
        {
            var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([new SandboxAction(kind, "db.query", target)])).RunAsync(spec, Ct);

            Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
            Assert.Equal("production-target", r.Violations[0].Reason);
        }
    }

    [Fact]
    public async Task The_runs_own_schema_is_reachable_with_a_grant()
    {
        var spec = Specs.Valid() with { Grants = [new SandboxPermissionGrant("db.query", "*")] };
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([new SandboxAction(SandboxActionKind.HostCall, "db.query", "db:sandbox_run_1.users")]))
            .RunAsync(spec, Ct);

        Assert.Equal(SandboxVerdict.Clean, r.Verdict);
    }

    [Fact]
    public async Task Timeout_kills_the_process_even_when_the_engine_ignores_its_token_and_hangs()
    {
        var provider = new FakeProvider(SandboxOs.Linux);
        var started = DateTime.UtcNow;

        var r = await Make(provider, new HangingEngine()).RunAsync(Specs.Valid(timeout: 1), Ct);

        Assert.Equal(SandboxVerdict.TimedOut, r.Verdict);
        Assert.True(provider.Last!.Kills >= 1);
        Assert.Equal(1, provider.Last.Disposals);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Timeout_holds_when_the_engine_ignores_its_token_and_keeps_emitting_allowed_actions()
    {
        var provider = new FakeProvider(SandboxOs.Linux);

        var r = await Make(provider, new FloodingEngine(Specs.GoodFile)).RunAsync(Specs.Valid(timeout: 1), Ct);

        Assert.Equal(SandboxVerdict.TimedOut, r.Verdict);
        Assert.True(provider.Last!.Kills >= 1);
    }

    [Fact]
    public async Task An_engine_that_throws_is_an_error_and_the_process_is_killed_and_disposed()
    {
        var provider = new FakeProvider(SandboxOs.Linux);

        var r = await Make(provider, new ScriptedEngine([Specs.GoodFile], new InvalidOperationException("boom"))).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.Error, r.Verdict);
        Assert.True(provider.Last!.Kills >= 1);
        Assert.Equal(1, provider.Last.Disposals);
    }

    [Fact]
    public async Task An_engine_that_throws_after_a_violation_keeps_the_violation_verdict()
    {
        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([Specs.BadFile], new InvalidOperationException("x"))).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
    }

    [Fact]
    public async Task A_provider_whose_kill_throws_does_not_turn_a_violation_into_clean()
    {
        var provider = new ThrowingKillProvider();

        var r = await new SandboxRunner([provider], [new ScriptedEngine([Specs.BadFile])], new FakeCompanyFactory(), null, SandboxOs.Linux).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
    }

    [Fact]
    public async Task Actions_emitted_after_a_violation_are_still_audited_but_a_flood_is_bounded()
    {
        var actions = new List<SandboxAction> { Specs.BadFile };
        actions.AddRange(Enumerable.Repeat(Specs.GoodFile, 5000));

        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine(actions)).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, r.Verdict);
        Assert.True(r.EventCount <= 300);
    }

    [Fact]
    public async Task A_canary_emitted_after_an_earlier_violation_still_wins()
    {
        var leak = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", FakeCompanyFactory.Canary);

        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([Specs.BadFile, leak])).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.CanaryTriggered, r.Verdict);
    }

    [Fact]
    public async Task A_canary_split_across_two_consecutive_payloads_is_caught()
    {
        var c = FakeCompanyFactory.Canary;
        var first = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", c[..6]);
        var second = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", c[6..]);

        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([first, second])).RunAsync(Specs.Valid(), Ct);

        Assert.Equal(SandboxVerdict.CanaryTriggered, r.Verdict);
    }

    [Fact]
    public async Task A_null_class_or_target_from_an_engine_is_a_denial_or_error_never_an_allow()
    {
        var weird = new SandboxAction(SandboxActionKind.FileAccess, null!, null!);

        var r = await Make(new FakeProvider(SandboxOs.Linux), new ScriptedEngine([weird])).RunAsync(Specs.Valid(), Ct);

        Assert.NotEqual(SandboxVerdict.Clean, r.Verdict);
    }

    [Fact]
    public void Replay_with_the_stored_head_detects_a_truncated_stream()
    {
        var trail = new HashChainedAuditTrail();
        trail.Append(Specs.GoodFile, SandboxDecision.Allowed, "granted");
        trail.Append(Specs.BadFile, SandboxDecision.Denied, "no-grant");
        var head = trail.HeadHash;
        var spec = Specs.Valid();
        var truncated = trail.Events.Take(1).ToArray();

        Assert.Equal(SandboxVerdict.Clean, SandboxRunner.Replay(spec, truncated, null).Verdict);
        Assert.Equal(SandboxVerdict.Error, SandboxRunner.Replay(spec, truncated, head).Verdict);
        Assert.Equal(SandboxVerdict.UndeclaredAccess, SandboxRunner.Replay(spec, trail.Events, head).Verdict);
    }

    private sealed class HangingEngine : ISandboxEngine
    {
        public SandboxEngineKind Kind => SandboxEngineKind.Wasm;

        public async IAsyncEnumerable<SandboxAction> RunAsync(
            SandboxRunSpec spec, ISandboxProcess process, SandboxTestCompany company, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            yield break;
        }
    }

    private sealed class FloodingEngine(SandboxAction action) : ISandboxEngine
    {
        public SandboxEngineKind Kind => SandboxEngineKind.Wasm;

        public async IAsyncEnumerable<SandboxAction> RunAsync(
            SandboxRunSpec spec, ISandboxProcess process, SandboxTestCompany company, [EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                await Task.Yield();
                yield return action;
            }
        }
    }

    private sealed class ThrowingKillProvider : ISandboxConfinementProvider
    {
        public SandboxOs Os => SandboxOs.Linux;

        public bool Tested => true;

        public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default) => Task.FromResult<ISandboxProcess>(new ThrowingProcess());

        private sealed class ThrowingProcess : ISandboxProcess
        {
            public Task KillAsync(CancellationToken ct = default) => throw new InvalidOperationException("kill failed");

            public ValueTask DisposeAsync() => throw new InvalidOperationException("dispose failed");
        }
    }
}
