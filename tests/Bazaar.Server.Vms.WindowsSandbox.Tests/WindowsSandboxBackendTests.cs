using System.Xml.Linq;
using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class WindowsSandboxBackendTests : IDisposable
{
    private static readonly Guid _hostBase = Guid.Parse("0196a5c0-0000-7000-8000-000000000001");

    private readonly FakeWindowsSandboxHost _host = new();
    private readonly WindowsSandboxBackend _backend;

    public WindowsSandboxBackendTests()
    {
        _backend = new WindowsSandboxBackend(_host, new FixedHostFacts(FixedHostFacts.WindowsWithSandbox()), new WindowsSandboxOptions(_hostBase));
    }

    public void Dispose() => _backend.Dispose();

    private static VmSpec WindowsSpec(int memoryMb = 4096) => new(VmOs.Windows, 2, memoryMb, 60);

    private async Task<VmHandle> CreateAsync() =>
        await _backend.CreateChildAsync(new ImageVersionId(_hostBase), WindowsSpec(), CancellationToken.None);

    [Fact]
    public void Probe_is_usable_on_windows_x64_with_the_feature()
    {
        ProviderStatus status = _backend.Probe();

        Assert.True(status.Usable);
        Assert.Equal("windows_sandbox", status.Kind);
        Assert.Null(status.Reason);
    }

    [Fact]
    public void Probe_refuses_a_linux_host_and_says_why()
    {
        var linux = new WindowsSandboxBackend(_host, new FixedHostFacts(new HostFacts("linux", "x64", new HashSet<string>(StringComparer.Ordinal))), new WindowsSandboxOptions(_hostBase));

        ProviderStatus status = linux.Probe();

        Assert.False(status.Usable);
        Assert.Contains("needs windows", status.Reason);
        Assert.Contains("this host is linux", status.Reason);
    }

    [Fact]
    public void Probe_refuses_when_the_feature_is_not_enabled()
    {
        var off = new WindowsSandboxBackend(_host, new FixedHostFacts(new HostFacts("windows", "x64", new HashSet<string>(StringComparer.Ordinal))), new WindowsSandboxOptions(_hostBase));

        ProviderStatus status = off.Probe();

        Assert.False(status.Usable);
        Assert.Contains("windows-sandbox", status.Reason);
    }

    [Fact]
    public void Probe_matches_the_module_requirements_it_declares()
    {
        Assert.Equal(["windows"], WindowsSandboxModule.Requirements.OperatingSystems);
        Assert.Empty(HostCompatibility.ValidateRequirements(WindowsSandboxModule.Requirements));
    }

    [Fact]
    public async Task Create_writes_a_config_with_networking_off_and_the_requested_memory()
    {
        VmHandle vm = await CreateAsync();

        string xml = Assert.Single(_host.Configs.Values);
        XElement root = XDocument.Parse(xml).Root!;
        Assert.Equal("Disable", root.Element("Networking")!.Value);
        Assert.Equal("4096", root.Element("MemoryInMB")!.Value);
        Assert.Equal("Disable", root.Element("VGpu")!.Value);
        Assert.Equal("Disable", root.Element("ClipboardRedirection")!.Value);
        Assert.Empty(root.Element("MappedFolders")!.Elements());
        Assert.StartsWith("wsb-", vm.BackendRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_refuses_a_linux_guest_before_writing_anything()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _backend.CreateChildAsync(new ImageVersionId(_hostBase), new VmSpec(VmOs.Linux, 2, 2048, 30), CancellationToken.None));

        Assert.Empty(_host.Configs);
    }

    [Fact]
    public async Task Create_refuses_a_parent_other_than_the_configured_host_base()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _backend.CreateChildAsync(new ImageVersionId(Guid.NewGuid()), WindowsSpec(), CancellationToken.None));

        Assert.Empty(_host.Configs);
    }

    [Fact]
    public async Task Start_launches_once_and_reports_running()
    {
        VmHandle vm = await CreateAsync();

        await _backend.StartAsync(vm, CancellationToken.None);

        Assert.Equal(VmState.Running, await _backend.GetStateAsync(vm, CancellationToken.None));
        Assert.Single(_host.Calls, c => c.StartsWith("launch:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_instance_is_refused_while_one_runs()
    {
        VmHandle first = await CreateAsync();
        VmHandle second = await CreateAsync();
        await _backend.StartAsync(first, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StartAsync(second, CancellationToken.None));

        Assert.Contains("allows 1 running instance", ex.Message);
        Assert.Equal(1, _host.Calls.Count(c => c.StartsWith("launch:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_sandbox_started_outside_the_server_also_blocks_a_start()
    {
        _host.ExternalInstances = 1;
        VmHandle vm = await CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StartAsync(vm, CancellationToken.None));

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("launch:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_graceful_and_force_reach_the_host_with_the_right_flag()
    {
        VmHandle vm = await CreateAsync();
        await _backend.StartAsync(vm, CancellationToken.None);

        await _backend.StopAsync(vm, StopMode.Graceful, CancellationToken.None);
        Assert.Equal(VmState.Stopped, await _backend.GetStateAsync(vm, CancellationToken.None));

        await _backend.StartAsync(vm, CancellationToken.None);
        await _backend.StopAsync(vm, StopMode.Force, CancellationToken.None);

        Assert.Contains(_host.Calls, c => c.EndsWith(":graceful", StringComparison.Ordinal));
        Assert.Contains(_host.Calls, c => c.EndsWith(":force", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_a_stopped_instance_is_refused()
    {
        VmHandle vm = await CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.StopAsync(vm, StopMode.Graceful, CancellationToken.None));
    }

    [Fact]
    public async Task A_sandbox_closed_from_outside_is_reported_stopped_and_frees_the_slot()
    {
        VmHandle first = await CreateAsync();
        VmHandle second = await CreateAsync();
        await _backend.StartAsync(first, CancellationToken.None);
        _host.CloseFromOutside("sandbox-1");

        Assert.Equal(VmState.Stopped, await _backend.GetStateAsync(first, CancellationToken.None));
        await _backend.StartAsync(second, CancellationToken.None);

        Assert.Equal(VmState.Running, await _backend.GetStateAsync(second, CancellationToken.None));
    }

    [Theory]
    [InlineData("save state")]
    [InlineData("checkpoint")]
    [InlineData("restore")]
    public async Task Save_checkpoint_and_restore_are_not_supported(string operation)
    {
        VmHandle vm = await CreateAsync();

        Exception ex = operation switch
        {
            "save state" => await Assert.ThrowsAsync<NotSupportedException>(() => _backend.SaveStateAsync(vm, CancellationToken.None)),
            "checkpoint" => await Assert.ThrowsAsync<NotSupportedException>(() => _backend.CheckpointAsync(vm, CancellationToken.None)),
            _ => await Assert.ThrowsAsync<NotSupportedException>(() => _backend.RestoreAsync(vm, new CheckpointId(Guid.NewGuid()), CancellationToken.None)),
        };

        Assert.Contains(operation, ex.Message);
    }

    [Fact]
    public void The_reported_capabilities_match_what_the_backend_refuses()
    {
        WindowsSandboxCapabilities caps = WindowsSandboxModule.Capabilities;

        Assert.False(caps.Save);
        Assert.False(caps.Checkpoint);
        Assert.False(caps.Restore);
        Assert.False(caps.LinuxGuests);
        Assert.True(caps.Ephemeral);
        Assert.Equal(1, caps.MaxConcurrentInstances);
        Assert.False(caps.DiskCapEnforced);
    }

    [Fact]
    public async Task Retire_stops_a_running_instance_forcibly_and_deletes_its_config()
    {
        VmHandle vm = await CreateAsync();
        await _backend.StartAsync(vm, CancellationToken.None);

        await _backend.RetireAsync(vm, CancellationToken.None);

        Assert.Equal(VmState.Retired, await _backend.GetStateAsync(vm, CancellationToken.None));
        Assert.Empty(_host.Configs);
        Assert.Contains(_host.Calls, c => c.EndsWith(":force", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_backend_ref_is_an_error()
    {
        var unknown = new VmHandle(Guid.NewGuid(), "wsb-missing");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _backend.GetStateAsync(unknown, CancellationToken.None));
    }

    [Fact]
    public void Config_builder_rejects_a_non_positive_memory_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsSandboxConfig.Build(WindowsSpec(memoryMb: 0), WindowsSandboxNetworking.Disable));
    }

    [Fact]
    public async Task Lifecycle_service_drives_create_start_stop_and_retire()
    {
        var store = new InMemoryVmStore();
        var service = new VmLifecycleService(store, _backend, new VmOperationLocks(), TimeProvider.System);
        var row = new EnvironmentVmRecord(Guid.NewGuid(), Guid.NewGuid(), VmOs.Windows, _hostBase, null, VmState.Absent, 2, 4096, 60, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.IsType<VmOperationResult.Applied>(await service.CreateAsync(row, CancellationToken.None));
        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(row.Id, CancellationToken.None));
        Assert.Equal(VmState.Running, store.StateOf(row.Id));

        await service.StopAsync(row.Id, StopMode.Graceful, CancellationToken.None);
        Assert.Equal(VmState.Stopped, store.StateOf(row.Id));

        await service.RetireAsync(row.Id, CancellationToken.None);
        Assert.Equal(VmState.Retired, store.StateOf(row.Id));
    }

    [Fact]
    public async Task Save_through_the_lifecycle_service_fails_and_leaves_the_recorded_state_unchanged()
    {
        var store = new InMemoryVmStore();
        var service = new VmLifecycleService(store, _backend, new VmOperationLocks(), TimeProvider.System);
        var row = new EnvironmentVmRecord(Guid.NewGuid(), Guid.NewGuid(), VmOs.Windows, _hostBase, null, VmState.Absent, 2, 4096, 60, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await service.CreateAsync(row, CancellationToken.None);
        await service.StartAsync(row.Id, CancellationToken.None);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.SaveAsync(row.Id, CancellationToken.None));

        Assert.Equal(VmState.Running, store.StateOf(row.Id));
    }
}
