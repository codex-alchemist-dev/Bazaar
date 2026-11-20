using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>The lifecycle service with several providers behind the router (doc 62 section 9.2).</summary>
public sealed class VmLifecycleRoutingTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeEnvironmentVmStore _store = new();
    private readonly FakeSource _pair = new("hyperv", VmProviderPlacement.Pair, VmBackendOrdering.PrimaryHypervisor, VmOs.Windows, VmOs.Linux);
    private readonly FakeSource _android = new("android", VmProviderPlacement.OnDemand, VmBackendOrdering.OnDemand, VmOs.Android);
    private readonly FakeGate _gate = new("android");

    private VmLifecycleService Service() =>
        new(_store, new VmBackendRouter([_pair, _android], new VmPairSelection("hyperv"), _gate), new VmOperationLocks(), TimeProvider.System);

    private static EnvironmentVmRecord Absent(VmOs os, string? kind = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), null, VmState.Absent, 2, 2048, 30, _epoch, _epoch, kind);

    [Fact]
    public async Task Create_records_the_routed_kind_and_each_vm_reaches_its_own_backend()
    {
        var service = Service();
        var linux = Absent(VmOs.Linux);
        var android = Absent(VmOs.Android);

        Assert.IsType<VmOperationResult.Applied>(await service.CreateAsync(linux, Ct));
        Assert.IsType<VmOperationResult.Applied>(await service.CreateAsync(android, Ct));
        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(android.Id, Ct));

        Assert.Equal("hyperv", (await _store.GetAsync(linux.Id, Ct))!.ProviderKind);
        Assert.Equal("android", (await _store.GetAsync(android.Id, Ct))!.ProviderKind);
        Assert.Equal(["Create"], _pair.Backend!.Calls.Select(c => c.Operation));
        Assert.Equal(["Create", "Start"], _android.Backend!.Calls.Select(c => c.Operation));
    }

    [Fact]
    public async Task A_create_no_provider_may_serve_is_refused_with_a_reason_and_writes_no_row()
    {
        _gate.Enabled.Clear();
        var vm = Absent(VmOs.Android);

        var result = await Service().CreateAsync(vm, Ct);

        var refusal = Assert.IsType<VmOperationResult.Refused>(result).Refusal;
        Assert.Equal(VmRefusalReason.NoProvider, refusal.Reason);
        Assert.Contains("vms.devices.enabled", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(await _store.GetAsync(vm.Id, Ct));
        Assert.Empty(_android.Backend!.Calls);
    }

    [Fact]
    public async Task An_operation_on_a_vm_whose_provider_is_missing_is_refused_without_a_backend_call()
    {
        var vm = Absent(VmOs.Linux, "kvm") with { State = VmState.Stopped, BackendRef = "ref-9" };
        _store.Seed(vm);

        var result = await Service().StartAsync(vm.Id, Ct);

        var refusal = Assert.IsType<VmOperationResult.Refused>(result).Refusal;
        Assert.Equal(VmRefusalReason.NoProvider, refusal.Reason);
        Assert.Equal(VmState.Stopped, _store.StateOf(vm.Id));
        Assert.Empty(_pair.Backend!.Calls);
    }

    [Fact]
    public async Task A_created_vm_keeps_its_backend_after_the_source_hands_out_a_new_one_until_it_retires()
    {
        var service = Service();
        var vm = Absent(VmOs.Android);
        await service.CreateAsync(vm, Ct);
        var original = _android.Backend!;

        _android.Backend = new FakeVmBackend();
        await service.StartAsync(vm.Id, Ct);
        await service.StopAsync(vm.Id, StopMode.Graceful, Ct);
        await service.RetireAsync(vm.Id, Ct);

        Assert.Equal(["Create", "Start", "Stop", "Retire"], original.Calls.Select(c => c.Operation));
        Assert.Empty(_android.Backend.Calls);
    }

    [Fact]
    public async Task Supports_save_state_is_asked_of_the_vms_own_backend()
    {
        var saving = new SavingBackend();
        var source = new FakeSource("hyperv", VmProviderPlacement.Pair, VmBackendOrdering.PrimaryHypervisor, VmOs.Windows) { Backend = null };
        var router = new VmBackendRouter([source, _android], new VmPairSelection("hyperv"), _gate);
        var service = new VmLifecycleService(_store, router, new VmOperationLocks(), TimeProvider.System);
        var windows = Absent(VmOs.Windows, "hyperv") with { State = VmState.Running, BackendRef = "w" };
        var android = Absent(VmOs.Android, "android") with { State = VmState.Running, BackendRef = "a" };
        _store.Seed(windows);
        _store.Seed(android);
        router.Pin(windows.Id, "hyperv", saving);

        Assert.True(await service.SupportsSaveStateAsync(windows.Id, Ct));
        Assert.False(await service.SupportsSaveStateAsync(android.Id, Ct));
        Assert.Null(await service.SupportsSaveStateAsync(Guid.CreateVersion7(), Ct));
    }

    private sealed class SavingBackend : IVmBackend
    {
        public bool SupportsSaveState => true;

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct) => Task.CompletedTask;

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => Task.CompletedTask;

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => Task.CompletedTask;

        public Task RetireAsync(VmHandle vm, CancellationToken ct) => Task.CompletedTask;

        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) => Task.FromResult(VmState.Running);
    }
}
