using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Abstractions;

namespace Bazaar.Server.VmDensity.Vms.Tests;

internal sealed class FakeDirectory(params AgentVmInfo[] vms) : IAgentVmDirectory
{
    public Exception? Failure { get; init; }

    public Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentVmInfo>>([.. vms.Where(v => v.AgentId == agentId)]);

    public Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Failure is null ? Task.FromResult<IReadOnlyList<AgentVmInfo>>(vms) : Task.FromException<IReadOnlyList<AgentVmInfo>>(Failure);
    }
}

internal sealed class FakeControl : IVmControl
{
    public List<Guid> Saved { get; } = [];

    public VmControlOutcome Outcome { get; set; } = VmControlOutcome.Applied;

    public Task<VmControlResult> SaveAsync(Guid vmId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Saved.Add(vmId);
        return Task.FromResult(new VmControlResult(Outcome, VmRunState.Saved));
    }

    public Task<VmControlResult> StopAsync(Guid vmId, VmStopMode mode, CancellationToken ct) => throw new NotSupportedException("The density adapter never stops a VM.");
}

internal sealed class FakeReader(Func<DensityVm, Task<VmActivity?>> read) : IVmActivityReader
{
    public Task<VmActivity?> ReadAsync(DensityVm vm, CancellationToken cancellationToken) => read(vm);
}

internal static class Vm
{
    public static readonly DateTimeOffset Epoch = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static AgentVmInfo Running(Guid? id = null, Guid? agent = null, VmGuestOs os = VmGuestOs.Linux, int memoryMb = 2048, bool? canSave = true, string? backendRef = "ref") =>
        new(id ?? Guid.CreateVersion7(), agent ?? Guid.CreateVersion7(), os, backendRef, VmRunState.Running, memoryMb, canSave);

    public static VmActivity Idle(TimeSpan? idleFor = null) => new(false, false, false, Epoch - (idleFor ?? TimeSpan.FromHours(1)));
}
