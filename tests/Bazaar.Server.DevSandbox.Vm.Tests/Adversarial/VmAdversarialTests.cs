namespace Bazaar.Server.DevSandbox.Vm.Tests.Adversarial;

public class VmAdversarialTests
{
    private static SandboxRunSpec Spec(string moduleId = "mod.example", IReadOnlyList<string>? egress = null) => new(
        "run-1", moduleId, "1.0.0", SandboxEngineKind.Vm, [], egress ?? [], 1UL, 30, 256);

    private sealed class Recording(FakeVmWorld inner) : ISandboxVmHost, ISandboxVmMounts
    {
        public List<SandboxVmRequest> Requests { get; } = [];

        public List<string> Bundles { get; } = [];

        public async Task<SandboxVmHandle> CreateAsync(SandboxVmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return await inner.CreateAsync(request, ct);
        }

        public Task StartAsync(SandboxVmHandle vm, CancellationToken ct) => inner.StartAsync(vm, ct);

        public Task StopAsync(SandboxVmHandle vm, CancellationToken ct) => inner.StopAsync(vm, ct);

        public Task RetireAsync(SandboxVmHandle vm, CancellationToken ct) => inner.RetireAsync(vm, ct);

        public Task AttachModuleBundleAsync(SandboxVmHandle vm, string bundleRef, CancellationToken ct)
        {
            Bundles.Add(bundleRef);
            return inner.AttachModuleBundleAsync(vm, bundleRef, ct);
        }

        public Task AttachScratchAsync(SandboxVmHandle vm, IReadOnlyDictionary<string, string> companyRecords, CancellationToken ct) =>
            inner.AttachScratchAsync(vm, companyRecords, ct);

        public Task DetachAllAsync(SandboxVmHandle vm, CancellationToken ct) => inner.DetachAllAsync(vm, ct);
    }

    [Fact]
    public async Task Nothing_the_module_names_reaches_the_vm_request_or_the_bundle_mount()
    {
        var world = new FakeVmWorld();
        var rec = new Recording(world);
        var engine = new VmSandboxEngine(world, rec, rec, world, world);
        var hostile = "..\\..\\Windows\\System32";

        await foreach (var unused in engine.RunAsync(Spec(hostile), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
        {
            _ = unused;
        }

        var request = Assert.Single(rec.Requests);
        Assert.Equal(new SandboxVmRequest("run-1", world.Plan!.Os, world.Plan.Image, 256), request);
        Assert.Equal([world.Plan.BundleRef], rec.Bundles);
        Assert.DoesNotContain("Windows", request.Image, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows", rec.Bundles[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guest_reported_host_paths_pass_through_as_data_and_are_denied_by_the_runner_with_teardown()
    {
        var world = new FakeVmWorld
        {
            Actions =
            [
                new SandboxAction(SandboxActionKind.FileAccess, "fs.read", "C:\\Users\\owner\\.ssh\\id_rsa"),
                new SandboxAction(SandboxActionKind.FileAccess, "fs.read", "/home/owner/souls/charles.md"),
            ],
        };
        var runner = new SandboxRunner(
            [new FakeProvider(SandboxOs.Linux)], [world.Engine()], new FakeCompanyFactory(), null, SandboxOs.Linux);

        var result = await runner.RunAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, result.Verdict);
        Assert.Equal(["block-all", "stop", "detach", "retire"], world.Log.Skip(world.Log.IndexOf("guest-run") + 1).ToArray());
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("svc.example.test.")]
    [InlineData(" svc.example.test")]
    [InlineData("svc.example.test ")]
    [InlineData("svc..example.test")]
    [InlineData("-svc.test")]
    [InlineData("svc-.test")]
    [InlineData("svc.test\n")]
    [InlineData("svc.test:443")]
    [InlineData("svc.example.testing")]
    [InlineData("*.test")]
    [InlineData(".test")]
    [InlineData("")]
    public async Task A_non_test_or_padded_egress_host_is_refused_before_any_vm_is_created(string host)
    {
        var world = new FakeVmWorld();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var unused in world.Engine().RunAsync(Spec(egress: [host]), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
            {
                _ = unused;
            }
        });

        Assert.Empty(world.Log);
    }

    [Fact]
    public async Task A_failed_mount_or_egress_step_means_the_guest_never_runs()
    {
        foreach (var step in new[] { "mount-bundle", "mount-scratch", "egress-apply", "block-all", "start" })
        {
            var world = new FakeVmWorld();
            world.Fail.Add(step);
            var egress = step == "block-all" ? Array.Empty<string>() : new[] { "svc.example.test" };

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var unused in world.Engine().RunAsync(Spec(egress: egress), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
                {
                    _ = unused;
                }
            });

            Assert.DoesNotContain("guest-run", world.Log);
            Assert.Contains("retire", world.Log);
        }
    }
}
