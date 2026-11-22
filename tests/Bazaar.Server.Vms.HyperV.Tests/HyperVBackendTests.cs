using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.HyperV.Tests;

public sealed class HyperVBackendTests
{
    private static readonly string _images = Path.Combine(Path.GetTempPath(), "bazaar-images");

    private readonly FakeHyperVHost _host = new();
    private readonly HyperVBackend _backend;

    public HyperVBackendTests()
    {
        _backend = new HyperVBackend(_host, new FixedParentResolver(_images), new FixedHostFacts(FixedHostFacts.WindowsWithHyperV()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VmSpec Spec(VmOs os = VmOs.Windows) => new(os, 2, 4096, 60);

    private async Task<VmHandle> NewVmAsync(HyperVVmState state)
    {
        var vm = await _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), Spec(), Ct);
        _host.Set(vm.BackendRef, state);
        return vm;
    }

    [Fact]
    public async Task Create_builds_a_differencing_disk_off_the_resolved_parent_then_the_vm()
    {
        var parent = new ImageVersionId(Guid.NewGuid());

        var vm = await _backend.CreateChildAsync(parent, Spec(VmOs.Linux), Ct);

        Assert.StartsWith("bazaar-", vm.BackendRef, StringComparison.Ordinal);
        Assert.True(HyperVNames.IsVmName(vm.BackendRef));
        Assert.Equal(Path.Combine(_images, parent.Value.ToString("N") + ".vhdx"), _host.ParentDisk);
        Assert.Equal<string>([$"disk:{vm.BackendRef}", $"create:{vm.BackendRef}"], _host.Calls);
        Assert.Equal(VmOs.Linux, _host.Created!.Os);
        Assert.Equal(4096, _host.Created.MemoryMb);
        Assert.Equal($"vms/{vm.BackendRef}/disk.vhdx", _host.Created.DiskPath);
    }

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(2, 256)]
    public async Task Create_refuses_a_spec_that_cannot_boot(int vcpu, int memoryMb)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), new VmSpec(VmOs.Windows, vcpu, memoryMb, 60), Ct));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task A_failed_create_removes_the_disk_and_reports_the_original_error()
    {
        _host.FailCreateVm = new InvalidOperationException("boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), Spec(), Ct));

        Assert.Equal("boom", ex.Message);
        Assert.Contains(_host.Calls, c => c.StartsWith("remove:", StringComparison.Ordinal) && c.EndsWith(":True", StringComparison.Ordinal));
    }

    [Fact]
    public void It_can_save_state_so_a_pause_keeps_the_agent()
    {
        Assert.True(_backend.SupportsSaveState);
        Assert.True(HyperVModule.Capabilities.Save);
    }

    [Fact]
    public async Task Start_works_from_off_and_from_saved_only()
    {
        var off = await NewVmAsync(HyperVVmState.Off);
        var saved = await NewVmAsync(HyperVVmState.Saved);
        var running = await NewVmAsync(HyperVVmState.Running);

        await _backend.StartAsync(off, Ct);
        await _backend.StartAsync(saved, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StartAsync(running, Ct));

        Assert.Equal(VmState.Running, await _backend.GetStateAsync(off, Ct));
        Assert.Equal(VmState.Running, await _backend.GetStateAsync(saved, Ct));
    }

    [Theory]
    [InlineData(StopMode.Graceful, "stop")]
    [InlineData(StopMode.Force, "stop-force")]
    public async Task Stop_maps_the_mode_and_needs_a_running_vm(StopMode mode, string call)
    {
        var running = await NewVmAsync(HyperVVmState.Running);
        var off = await NewVmAsync(HyperVVmState.Off);

        await _backend.StopAsync(running, mode, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StopAsync(off, mode, Ct));

        Assert.Contains($"{call}:{running.BackendRef}", _host.Calls);
        Assert.Equal(VmState.Stopped, await _backend.GetStateAsync(running, Ct));
    }

    [Fact]
    public async Task Save_stops_a_running_vm_into_saved_state()
    {
        var running = await NewVmAsync(HyperVVmState.Running);

        await _backend.SaveStateAsync(running, Ct);

        Assert.Equal(VmState.Saved, await _backend.GetStateAsync(running, Ct));
    }

    [Fact]
    public async Task Save_refuses_a_vm_that_is_not_running()
    {
        var off = await NewVmAsync(HyperVVmState.Off);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.SaveStateAsync(off, Ct));
    }

    [Fact]
    public async Task Checkpoint_and_restore_need_a_stopped_or_saved_vm_and_use_one_name()
    {
        var off = await NewVmAsync(HyperVVmState.Off);
        var running = await NewVmAsync(HyperVVmState.Running);

        var id = await _backend.CheckpointAsync(off, Ct);
        await _backend.RestoreAsync(off, id, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.CheckpointAsync(running, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.RestoreAsync(running, id, Ct));

        var name = HyperVNames.Checkpoint(id.Value);
        Assert.Contains($"checkpoint:{off.BackendRef}:{name}", _host.Calls);
        Assert.Contains($"restore:{off.BackendRef}:{name}", _host.Calls);
    }

    [Fact]
    public async Task Retire_turns_a_running_vm_off_before_removing_it_and_its_disk()
    {
        var running = await NewVmAsync(HyperVVmState.Running);
        _host.Calls.Clear();

        await _backend.RetireAsync(running, Ct);

        Assert.Equal<string>([$"stop-force:{running.BackendRef}", $"remove:{running.BackendRef}:True"], _host.Calls);
        Assert.Equal(VmState.Absent, await _backend.GetStateAsync(running, Ct));
    }

    [Fact]
    public async Task Retire_is_safe_for_a_vm_that_is_already_gone()
    {
        var vm = await NewVmAsync(HyperVVmState.Off);
        await _backend.RetireAsync(vm, Ct);

        await _backend.RetireAsync(vm, Ct);

        Assert.Equal(VmState.Absent, await _backend.GetStateAsync(vm, Ct));
    }

    [Fact]
    public async Task A_vm_that_hyper_v_does_not_know_is_reported_missing_not_created()
    {
        var vm = new VmHandle(Guid.NewGuid(), HyperVNames.Vm(Guid.NewGuid()));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _backend.StartAsync(vm, Ct));
        Assert.Equal(VmState.Absent, await _backend.GetStateAsync(vm, Ct));
    }

    [Fact]
    public async Task A_backend_ref_this_provider_did_not_create_is_refused()
    {
        var vm = new VmHandle(Guid.NewGuid(), "my-other-vm; Remove-Item C:\\");

        await Assert.ThrowsAsync<ArgumentException>(() => _backend.StartAsync(vm, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _backend.RetireAsync(vm, Ct));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task A_transitioning_vm_is_not_given_a_made_up_state()
    {
        var vm = await NewVmAsync(HyperVVmState.Transitioning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.GetStateAsync(vm, Ct));
    }

    [Fact]
    public void Probe_is_usable_only_on_a_windows_host_with_hyper_v()
    {
        var ok = _backend.Probe();
        var noFeature = new HyperVBackend(_host, new FixedParentResolver(_images), new FixedHostFacts(new("windows", "x64", new HashSet<string>()))).Probe();
        var linux = new HyperVBackend(_host, new FixedParentResolver(_images), new FixedHostFacts(new("linux", "x64", new HashSet<string>(["hyperv"])))).Probe();

        Assert.True(ok.Usable);
        Assert.Equal("hyperv", ok.Kind);
        Assert.False(noFeature.Usable);
        Assert.False(linux.Usable);
    }
}
