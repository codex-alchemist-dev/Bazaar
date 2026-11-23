using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Kvm.Tests;

public sealed class KvmBackendTests
{
    private static readonly string _images = Path.Combine(Path.GetTempPath(), "bazaar-images");

    private readonly FakeKvmHost _host = new();
    private readonly KvmBackend _backend;

    public KvmBackendTests()
    {
        _backend = new KvmBackend(_host, new FixedParentResolver(_images), new FixedHostFacts(FixedHostFacts.LinuxWithKvm()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VmSpec Spec(VmOs os = VmOs.Linux) => new(os, 2, 4096, 60);

    private async Task<VmHandle> NewVmAsync(KvmDomainState state)
    {
        var vm = await _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), Spec(), Ct);
        _host.Set(vm.BackendRef, state);
        return vm;
    }

    [Fact]
    public async Task Create_builds_a_backed_disk_off_the_resolved_parent_then_the_domain()
    {
        var parent = new ImageVersionId(Guid.NewGuid());

        var vm = await _backend.CreateChildAsync(parent, Spec(VmOs.Linux), Ct);

        Assert.StartsWith("bazaar-", vm.BackendRef, StringComparison.Ordinal);
        Assert.True(KvmNames.IsDomainName(vm.BackendRef));
        Assert.Equal(Path.Combine(_images, parent.Value.ToString("N") + ".qcow2"), _host.ParentDisk);
        Assert.Equal<string>([$"disk:{vm.BackendRef}", $"define:{vm.BackendRef}"], _host.Calls);
        Assert.Equal(VmOs.Linux, _host.Defined!.Os);
        Assert.Equal(2, _host.Defined.Vcpu);
        Assert.Equal(4096, _host.Defined.MemoryMb);
        Assert.Equal($"vms/{vm.BackendRef}/disk.qcow2", _host.Defined.DiskPath);
    }

    [Fact]
    public async Task Create_refuses_a_windows_guest_before_touching_the_host()
    {
        // KvmModule.Capabilities.WindowsGuests is false: no guest transport exists for Windows on a Linux host yet (doc 16, OPEN).
        Assert.False(KvmModule.Capabilities.WindowsGuests);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), Spec(VmOs.Windows), Ct));

        Assert.Empty(_host.Calls);
        Assert.Null(_host.ParentDisk);
        Assert.Null(_host.Defined);
    }

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(2, 256)]
    public async Task Create_refuses_a_spec_that_cannot_boot(int vcpu, int memoryMb)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), new VmSpec(VmOs.Linux, vcpu, memoryMb, 60), Ct));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task A_failed_define_removes_the_disk_and_reports_the_original_error()
    {
        _host.FailDefine = new InvalidOperationException("boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), Spec(), Ct));

        Assert.Equal("boom", ex.Message);
        Assert.Contains(_host.Calls, c => c.StartsWith("remove:", StringComparison.Ordinal) && c.EndsWith(":True", StringComparison.Ordinal));
    }

    [Fact]
    public void It_can_save_state_so_a_pause_keeps_the_agent()
    {
        Assert.True(_backend.SupportsSaveState);
        Assert.True(KvmModule.Capabilities.Save);
    }

    [Fact]
    public async Task Start_works_from_off_and_from_saved_only()
    {
        var off = await NewVmAsync(KvmDomainState.Off);
        var saved = await NewVmAsync(KvmDomainState.Saved);
        var running = await NewVmAsync(KvmDomainState.Running);

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
        var running = await NewVmAsync(KvmDomainState.Running);
        var off = await NewVmAsync(KvmDomainState.Off);

        await _backend.StopAsync(running, mode, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StopAsync(off, mode, Ct));

        Assert.Contains($"{call}:{running.BackendRef}", _host.Calls);
        Assert.Equal(VmState.Stopped, await _backend.GetStateAsync(running, Ct));
    }

    [Fact]
    public async Task A_force_stop_works_on_a_suspended_or_hung_vm_but_a_graceful_stop_does_not()
    {
        var stuck = await NewVmAsync(KvmDomainState.Transitioning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StopAsync(stuck, StopMode.Graceful, Ct));
        await _backend.StopAsync(stuck, StopMode.Force, Ct);

        Assert.Contains($"stop-force:{stuck.BackendRef}", _host.Calls);
        Assert.Equal(VmState.Stopped, await _backend.GetStateAsync(stuck, Ct));
    }

    [Fact]
    public async Task Save_stops_a_running_vm_into_saved_state()
    {
        var running = await NewVmAsync(KvmDomainState.Running);

        await _backend.SaveStateAsync(running, Ct);

        Assert.Equal(VmState.Saved, await _backend.GetStateAsync(running, Ct));
    }

    [Fact]
    public async Task Save_refuses_a_vm_that_is_not_running()
    {
        var off = await NewVmAsync(KvmDomainState.Off);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.SaveStateAsync(off, Ct));
    }

    [Fact]
    public async Task Checkpoint_and_restore_need_a_stopped_or_saved_vm_and_use_one_name()
    {
        var off = await NewVmAsync(KvmDomainState.Off);
        var running = await NewVmAsync(KvmDomainState.Running);

        var id = await _backend.CheckpointAsync(off, Ct);
        await _backend.RestoreAsync(off, id, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.CheckpointAsync(running, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.RestoreAsync(running, id, Ct));

        var name = KvmNames.Checkpoint(id.Value);
        Assert.Contains($"checkpoint:{off.BackendRef}:{name}", _host.Calls);
        Assert.Contains($"restore:{off.BackendRef}:{name}", _host.Calls);
    }

    [Fact]
    public async Task Retire_turns_a_running_vm_off_before_removing_it_and_its_disk()
    {
        var running = await NewVmAsync(KvmDomainState.Running);
        _host.Calls.Clear();

        await _backend.RetireAsync(running, Ct);

        Assert.Equal<string>([$"stop-force:{running.BackendRef}", $"remove:{running.BackendRef}:True"], _host.Calls);
        Assert.Equal(VmState.Absent, await _backend.GetStateAsync(running, Ct));
    }

    [Fact]
    public async Task Retire_is_safe_for_a_vm_that_is_already_gone()
    {
        var vm = await NewVmAsync(KvmDomainState.Off);
        await _backend.RetireAsync(vm, Ct);

        await _backend.RetireAsync(vm, Ct);

        Assert.Equal(VmState.Absent, await _backend.GetStateAsync(vm, Ct));
    }

    [Fact]
    public async Task A_domain_that_libvirt_does_not_know_is_reported_missing_not_created()
    {
        var vm = new VmHandle(Guid.NewGuid(), KvmNames.Domain(Guid.NewGuid()));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _backend.StartAsync(vm, Ct));
        Assert.Equal(VmState.Absent, await _backend.GetStateAsync(vm, Ct));
    }

    [Fact]
    public async Task A_backend_ref_this_provider_did_not_create_is_refused()
    {
        var vm = new VmHandle(Guid.NewGuid(), "my-other-vm; virsh destroy --all");

        await Assert.ThrowsAsync<ArgumentException>(() => _backend.StartAsync(vm, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _backend.RetireAsync(vm, Ct));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task A_transitioning_vm_is_not_given_a_made_up_state()
    {
        var vm = await NewVmAsync(KvmDomainState.Transitioning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.GetStateAsync(vm, Ct));
    }

    [Fact]
    public void Probe_is_usable_only_on_a_linux_host_with_kvm()
    {
        var ok = _backend.Probe();
        var noFeature = new KvmBackend(_host, new FixedParentResolver(_images), new FixedHostFacts(new("linux", "x64", new HashSet<string>()))).Probe();
        var windows = new KvmBackend(_host, new FixedParentResolver(_images), new FixedHostFacts(new("windows", "x64", new HashSet<string>(["kvm"])))).Probe();

        Assert.True(ok.Usable);
        Assert.Equal("kvm", ok.Kind);
        Assert.Equal<string>(["save-state", "checkpoint", "linux-guests"], ok.Features);
        Assert.False(noFeature.Usable);
        Assert.False(windows.Usable);
    }
}
