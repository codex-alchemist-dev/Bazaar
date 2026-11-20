using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.MicroVm.Tests;

public sealed class FakeMonitor : IMicroVmMonitor, IMicroVmHostFacts
{
    public HashSet<string> Running { get; } = [];
    public List<string> Calls { get; } = [];
    public HostFacts Facts { get; set; } = new("linux", "x64", new HashSet<string> { "kvm", "microvm" });
    public HostFacts Current() => Facts;
    public Task CreateDiskAsync(string name, int vcpu, int memoryMb, int diskCapGb, CancellationToken ct) { Calls.Add("disk " + name); return Task.CompletedTask; }
    public Task BootAsync(string name, CancellationToken ct) { Running.Add(name); return Task.CompletedTask; }
    public Task StopAsync(string name, bool force, CancellationToken ct) { Running.Remove(name); Calls.Add($"stop {force}"); return Task.CompletedTask; }
    public Task<bool> IsRunningAsync(string name, CancellationToken ct) => Task.FromResult(Running.Contains(name));
    public Task DeleteAsync(string name, CancellationToken ct) { Calls.Add("delete"); return Task.CompletedTask; }
}

public sealed class MicroVmBackendTests
{
    private static readonly Guid _image = Guid.CreateVersion7();
    private static readonly VmSpec _spec = new(VmOs.Linux, 1, 512, 4);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MicroVmBackend B, FakeMonitor M) Make(int max = 2)
    {
        var m = new FakeMonitor();
        var state = OperatingSystem.IsWindows() ? @"V:\mv" : "/var/lib/bazaar/mv";
        return (new MicroVmBackend(m, m, new MicroVmOptions { ImageVersion = _image, StateDirectory = state, MaxConcurrent = max }), m);
    }

    [Fact]
    public async Task Create_refuses_windows_and_unpinned_image()
    {
        var (b, _) = Make();
        await Assert.ThrowsAsync<NotSupportedException>(() => b.CreateChildAsync(new(_image), _spec with { Os = VmOs.Windows }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateChildAsync(new(Guid.NewGuid()), _spec, Ct));
    }

    [Fact]
    public async Task Nothing_runs_until_start_and_cap_holds()
    {
        var (b, m) = Make(1);
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        Assert.Empty(m.Running);
        await b.StartAsync(a, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(c, Ct));
        await b.StopAsync(a, StopMode.Graceful, Ct);
        await b.StartAsync(c, Ct);
        Assert.Single(m.Running);
    }

    [Fact]
    public async Task Save_checkpoint_restore_are_never_faked()
    {
        var (b, _) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        Assert.False(((IVmBackend)b).SupportsSaveState);
        await Assert.ThrowsAsync<NotSupportedException>(() => b.SaveStateAsync(vm, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.CheckpointAsync(vm, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.RestoreAsync(vm, default, Ct));
        Assert.False(MicroVmModule.Capabilities.Save || MicroVmModule.Capabilities.EgressEnforced);
    }

    [Fact]
    public async Task External_exit_and_retire()
    {
        var (b, m) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);
        m.Running.Clear();
        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, Ct));
        await b.StartAsync(vm, Ct);
        await b.RetireAsync(vm, Ct);
        Assert.Equal(VmState.Retired, await b.GetStateAsync(vm, Ct));
        Assert.Contains("delete", m.Calls);
    }

    [Fact]
    public void Probe_needs_linux_kvm_and_microvm()
    {
        var (b, m) = Make();
        Assert.True(b.Probe().Usable);
        m.Facts = new("windows", "x64", new HashSet<string> { "kvm", "microvm" });
        Assert.False(b.Probe().Usable);
        m.Facts = new("linux", "x64", new HashSet<string> { "kvm" });
        Assert.False(b.Probe().Usable);
        Assert.Empty(HostCompatibility.ValidateRequirements(MicroVmModule.Requirements));
    }

    [Fact]
    public void Options_need_absolute_state_directory()
    {
        Assert.Throws<InvalidOperationException>(new MicroVmOptions { ImageVersion = _image, StateDirectory = "rel" }.Validate);
    }
}
