using System.Runtime.CompilerServices;

namespace Bazaar.Server.DevSandbox.Vm.Tests;

internal sealed class FakeProcess : ISandboxProcess
{
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProvider(SandboxOs os) : ISandboxConfinementProvider
{
    public SandboxOs Os { get; } = os;

    public bool Tested => true;

    public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default) =>
        Task.FromResult<ISandboxProcess>(new FakeProcess());
}

internal sealed class FakeCompanyFactory : ISandboxCompanyFactory
{
    public SandboxTestCompany Create(SandboxRunSpec spec) => Company();

    public static SandboxTestCompany Company() => new(
        "sandbox-run-1",
        "sandbox_run_1",
        "sandbox_run_1",
        new Dictionary<string, string> { ["people"] = "synthetic people" },
        ["CANARY-XYZ"]);
}

/// <summary>One fake standing in for all five seams, writing to one ordered log. Steps named in <see cref="Fail"/> throw.</summary>
internal sealed class FakeVmWorld : ISandboxVmModuleSource, ISandboxVmHost, ISandboxVmMounts, ISandboxVmEgress, ISandboxVmGuestChannel
{
    private int _next;

    public List<string> Log { get; } = [];

    public HashSet<string> Fail { get; } = [];

    public bool ReuseIds { get; set; }

    public SandboxVmModulePlan? Plan { get; set; } = new(SandboxVmOs.Linux, "linux-golden", "bundle://mod.example/1.0.0", "/bundle/run");

    public List<SandboxAction> Actions { get; set; } = [];

    public IReadOnlyDictionary<string, string>? Scratch { get; private set; }

    public IReadOnlyList<string>? AppliedHosts { get; private set; }

    public Func<CancellationToken, Task>? DuringRun { get; set; }

    public int Count(string entry) => Log.Count(e => e == entry);

    private void Step(string name)
    {
        Log.Add(name);
        if (Fail.Contains(name))
        {
            throw new InvalidOperationException("injected " + name);
        }
    }

    public ValueTask<SandboxVmModulePlan?> GetPlanAsync(string moduleId, string moduleVersion, CancellationToken ct) => ValueTask.FromResult(Plan);

    public Task<SandboxVmHandle> CreateAsync(SandboxVmRequest request, CancellationToken ct)
    {
        Step("create");
        return Task.FromResult(new SandboxVmHandle(ReuseIds ? "vm-fixed" : "vm-" + Interlocked.Increment(ref _next)));
    }

    public Task StartAsync(SandboxVmHandle vm, CancellationToken ct)
    {
        Step("start");
        return Task.CompletedTask;
    }

    public Task StopAsync(SandboxVmHandle vm, CancellationToken ct)
    {
        Step("stop");
        return Task.CompletedTask;
    }

    public Task RetireAsync(SandboxVmHandle vm, CancellationToken ct)
    {
        Step("retire");
        return Task.CompletedTask;
    }

    public Task AttachModuleBundleAsync(SandboxVmHandle vm, string bundleRef, CancellationToken ct)
    {
        Step("mount-bundle");
        return Task.CompletedTask;
    }

    public Task AttachScratchAsync(SandboxVmHandle vm, IReadOnlyDictionary<string, string> companyRecords, CancellationToken ct)
    {
        Step("mount-scratch");
        Scratch = companyRecords;
        return Task.CompletedTask;
    }

    public Task DetachAllAsync(SandboxVmHandle vm, CancellationToken ct)
    {
        Step("detach");
        return Task.CompletedTask;
    }

    public Task ApplyAsync(SandboxVmHandle vm, IReadOnlyList<string> allowedTestHosts, CancellationToken ct)
    {
        Step("egress-apply");
        AppliedHosts = allowedTestHosts;
        return Task.CompletedTask;
    }

    public Task BlockAllAsync(SandboxVmHandle vm, CancellationToken ct)
    {
        Step("block-all");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SandboxAction> RunAsync(SandboxVmHandle vm, SandboxVmModulePlan plan, [EnumeratorCancellation] CancellationToken ct)
    {
        Step("guest-run");
        foreach (var a in Actions)
        {
            await Task.Yield();
            yield return a;
        }

        if (DuringRun is not null)
        {
            await DuringRun(ct);
        }
    }

    public VmSandboxEngine Engine(VmEngineOptions? options = null) => new(this, this, this, this, this, options);
}

public class VmEngineTests
{
    private static readonly SandboxAction _goodNet = new(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "hello");
    private static readonly SandboxAction _goodFile = new(SandboxActionKind.FileAccess, "fs.read", "/scratch/people");
    private static readonly SandboxAction _badFile = new(SandboxActionKind.FileAccess, "fs.read", "/etc/shadow");

    private static SandboxRunSpec Spec(IReadOnlyList<string>? egress = null) => new(
        "run-1",
        "mod.example",
        "1.0.0",
        SandboxEngineKind.Vm,
        [new SandboxPermissionGrant("fs.read", "/scratch/*"), new SandboxPermissionGrant("net.egress", "svc.example.test")],
        egress ?? ["svc.example.test"],
        42UL,
        30,
        256);

    private static async Task<List<SandboxAction>> DrainAsync(FakeVmWorld w, SandboxRunSpec spec, CancellationToken ct = default)
    {
        var list = new List<SandboxAction>();
        await foreach (var a in w.Engine().RunAsync(spec, new FakeProcess(), FakeCompanyFactory.Company(), ct))
        {
            list.Add(a);
        }

        return list;
    }

    [Fact]
    public async Task Mounts_and_egress_come_before_start_and_start_before_guest_run()
    {
        var w = new FakeVmWorld();
        await DrainAsync(w, Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["create", "mount-bundle", "mount-scratch", "egress-apply", "start", "guest-run", "block-all", "stop", "detach", "retire"],
            w.Log);
        Assert.Equal(["svc.example.test"], w.AppliedHosts);
    }

    [Fact]
    public async Task Empty_allowlist_confirms_block_all_before_start_and_applies_nothing()
    {
        var w = new FakeVmWorld();
        await DrainAsync(w, Spec([]), TestContext.Current.CancellationToken);

        Assert.True(w.Log.IndexOf("block-all") < w.Log.IndexOf("start"));
        Assert.DoesNotContain("egress-apply", w.Log);
        Assert.Null(w.AppliedHosts);
    }

    [Fact]
    public async Task Scratch_holds_only_the_test_company_records()
    {
        var w = new FakeVmWorld();
        await DrainAsync(w, Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(FakeCompanyFactory.Company().Records, w.Scratch);
    }

    [Theory]
    [InlineData("mount-bundle")]
    [InlineData("mount-scratch")]
    [InlineData("egress-apply")]
    public async Task Mount_or_egress_failure_means_no_start_and_the_vm_is_retired(string failing)
    {
        var w = new FakeVmWorld();
        w.Fail.Add(failing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(w, Spec(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("start", w.Log);
        Assert.DoesNotContain("guest-run", w.Log);
        Assert.Equal(1, w.Count("retire"));
        Assert.Equal(1, w.Count("detach"));
        Assert.True(w.Count("block-all") >= 1);
    }

    [Fact]
    public async Task Block_all_failure_with_empty_allowlist_means_no_start()
    {
        var w = new FakeVmWorld();
        w.Fail.Add("block-all");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(w, Spec([]), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("start", w.Log);
        Assert.Contains("retire", w.Log);
    }

    [Fact]
    public async Task Start_failure_retires_the_vm()
    {
        var w = new FakeVmWorld();
        w.Fail.Add("start");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(w, Spec(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("guest-run", w.Log);
        Assert.Equal(1, w.Count("retire"));
    }

    [Fact]
    public async Task Create_failure_starts_nothing()
    {
        var w = new FakeVmWorld();
        w.Fail.Add("create");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(w, Spec(), TestContext.Current.CancellationToken));

        Assert.Equal(["create"], w.Log);
    }

    [Fact]
    public async Task Guest_failure_still_tears_down()
    {
        var w = new FakeVmWorld { DuringRun = _ => throw new IOException("guest died") };

        await Assert.ThrowsAsync<IOException>(() => DrainAsync(w, Spec(), TestContext.Current.CancellationToken));

        Assert.Equal(1, w.Count("block-all"));
        Assert.Equal(1, w.Count("detach"));
        Assert.Equal(1, w.Count("retire"));
    }

    [Fact]
    public async Task Cancel_still_blocks_detaches_and_retires()
    {
        using var cts = new CancellationTokenSource();
        var w = new FakeVmWorld { DuringRun = async ct => await Task.Delay(Timeout.Infinite, ct) };
        var task = DrainAsync(w, Spec(), cts.Token);
        while (!w.Log.Contains("guest-run"))
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(1, w.Count("block-all"));
        Assert.Equal(1, w.Count("stop"));
        Assert.Equal(1, w.Count("detach"));
        Assert.Equal(1, w.Count("retire"));
    }

    [Fact]
    public async Task A_failing_teardown_step_does_not_skip_the_others_and_is_reported()
    {
        var w = new FakeVmWorld();
        w.Fail.Add("detach");
        var reported = new List<string>();
        var engine = w.Engine(new VmEngineOptions { OnCleanupFailure = (_, step, _) => reported.Add(step) });

        await foreach (var unused in engine.RunAsync(Spec(), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
        {
            _ = unused;
        }

        Assert.Equal(["detach"], reported);
        Assert.Equal(1, w.Count("retire"));
    }

    [Fact]
    public async Task A_failing_retire_is_retried_once()
    {
        var w = new FakeVmWorld();
        w.Fail.Add("retire");

        await DrainAsync(w, Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(2, w.Count("retire"));
    }

    [Fact]
    public async Task Each_run_gets_a_fresh_vm_and_a_reused_vm_is_refused()
    {
        var w = new FakeVmWorld();
        await DrainAsync(w, Spec(), TestContext.Current.CancellationToken);
        await DrainAsync(w, Spec(), TestContext.Current.CancellationToken);
        Assert.Equal(2, w.Count("create"));
        Assert.Equal(2, w.Count("retire"));

        var reused = new FakeVmWorld { ReuseIds = true };
        var engine = reused.Engine();
        await foreach (var unused in engine.RunAsync(Spec(), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
        {
            _ = unused;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var unused in engine.RunAsync(Spec(), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
            {
                _ = unused;
            }
        });
        Assert.Equal(1, reused.Count("start"));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("*.test")]
    [InlineData(".test")]
    [InlineData("a.test/path")]
    public async Task Non_test_egress_hosts_are_refused_before_a_vm_is_created(string host)
    {
        var w = new FakeVmWorld();

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(w, Spec([host]), TestContext.Current.CancellationToken));

        Assert.Empty(w.Log);
    }

    [Fact]
    public async Task Unknown_module_fails_closed_without_creating_a_vm()
    {
        var w = new FakeVmWorld { Plan = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(w, Spec(), TestContext.Current.CancellationToken));

        Assert.Empty(w.Log);
    }

    [Fact]
    public async Task Guest_actions_are_yielded_unchanged_and_in_order()
    {
        var w = new FakeVmWorld { Actions = [_goodFile, _goodNet, _badFile] };

        var actions = await DrainAsync(w, Spec(), TestContext.Current.CancellationToken);

        Assert.Equal([_goodFile, _goodNet, _badFile], actions);
    }

    [Fact]
    public void Engine_kind_is_vm()
    {
        Assert.Equal(SandboxEngineKind.Vm, new FakeVmWorld().Engine().Kind);
    }

    private static SandboxRunner Runner(FakeVmWorld w) =>
        new([new FakeProvider(SandboxOs.Linux)], [w.Engine()], new FakeCompanyFactory(), null, SandboxOs.Linux);

    [Fact]
    public async Task Runner_gives_clean_for_granted_actions()
    {
        var w = new FakeVmWorld { Actions = [_goodFile, _goodNet] };

        var result = await Runner(w).RunAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.Clean, result.Verdict);
        Assert.Equal(1, w.Count("retire"));
    }

    [Fact]
    public async Task Runner_gives_undeclared_access_then_blocks_all_and_retires()
    {
        var w = new FakeVmWorld { Actions = [_goodFile, _badFile, _goodFile] };

        var result = await Runner(w).RunAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, result.Verdict);
        var tail = w.Log.Skip(w.Log.IndexOf("guest-run") + 1).ToList();
        Assert.Equal(["block-all", "stop", "detach", "retire"], tail);
    }

    [Fact]
    public async Task Runner_gives_error_and_nothing_starts_when_egress_fails()
    {
        var w = new FakeVmWorld { Actions = [_goodFile] };
        w.Fail.Add("egress-apply");

        var result = await Runner(w).RunAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.Error, result.Verdict);
        Assert.DoesNotContain("start", w.Log);
        Assert.Equal(1, w.Count("retire"));
    }

    [Fact]
    public async Task Runner_flags_a_guest_that_reports_a_canary()
    {
        var leak = new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "x CANARY-XYZ");
        var w = new FakeVmWorld { Actions = [leak] };

        var result = await Runner(w).RunAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.CanaryTriggered, result.Verdict);
        Assert.Equal(1, w.Count("retire"));
    }
}
