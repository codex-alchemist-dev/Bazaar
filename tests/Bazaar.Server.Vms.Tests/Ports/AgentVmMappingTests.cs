using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Ports;

namespace Bazaar.Server.Vms.Tests.Ports;

public sealed class AgentVmMappingTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static EnvironmentVmRecord Record(VmState state, string? backendRef = "vm-1", VmOs os = VmOs.Linux) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), backendRef, state, 2, 2048, 30, _epoch, _epoch);

    [Theory]
    [InlineData(VmState.Absent, VmRunState.Provisioning)]
    [InlineData(VmState.Provisioning, VmRunState.Provisioning)]
    [InlineData(VmState.Stopped, VmRunState.Stopped)]
    [InlineData(VmState.Running, VmRunState.Running)]
    [InlineData(VmState.Saved, VmRunState.Saved)]
    [InlineData(VmState.Checkpointed, VmRunState.Other)]
    [InlineData(VmState.Restored, VmRunState.Other)]
    public void Every_non_retired_state_maps_to_a_run_state(VmState state, VmRunState expected) =>
        Assert.Equal(expected, AgentVmMapping.ToRunState(state));

    [Fact]
    public void Retired_has_no_run_state_and_no_info()
    {
        Assert.Null(AgentVmMapping.ToRunState(VmState.Retired));
        Assert.Null(AgentVmMapping.ToInfo(Record(VmState.Retired)));
    }

    [Fact]
    public void Every_vm_state_is_mapped_or_deliberately_retired() =>
        Assert.All(Enum.GetValues<VmState>(), s => Assert.Equal(s == VmState.Retired, AgentVmMapping.ToRunState(s) is null));

    [Theory]
    [InlineData(VmOs.Windows, VmGuestOs.Windows)]
    [InlineData(VmOs.Linux, VmGuestOs.Linux)]
    public void Guest_os_maps_one_to_one(VmOs os, VmGuestOs expected) => Assert.Equal(expected, AgentVmMapping.ToGuestOs(os));

    [Fact]
    public void A_running_record_keeps_its_ids_os_and_backend_ref()
    {
        var record = Record(VmState.Running, "hyperv-name", VmOs.Windows);

        var info = AgentVmMapping.ToInfo(record);

        Assert.Equal(new AgentVmInfo(record.Id, record.AgentId, VmGuestOs.Windows, "hyperv-name", VmRunState.Running, 2048), info);
        Assert.Null(info!.CanSaveState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_running_record_without_a_backend_ref_is_never_reported_as_running(string? backendRef)
    {
        var info = AgentVmMapping.ToInfo(Record(VmState.Running, backendRef));

        Assert.NotNull(info);
        Assert.Equal(VmRunState.Other, info.State);
        Assert.Null(info.BackendRef);
    }

    [Fact]
    public void A_saved_record_keeps_a_null_backend_ref_as_null() =>
        Assert.Null(AgentVmMapping.ToInfo(Record(VmState.Saved, null))!.BackendRef);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_memory_comes_from_the_record_and_the_save_fact_is_passed_through(bool canSave)
    {
        var record = Record(VmState.Running);

        var info = AgentVmMapping.ToInfo(record, canSave);

        Assert.NotNull(info);
        Assert.Equal(2048, info.MemoryMb);
        Assert.Equal(canSave, info.CanSaveState);
    }
}
