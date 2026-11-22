namespace Bazaar.Server.Vms.HyperV.Tests;

/// <summary>An in-memory <see cref="IHyperVHost"/>. Records every call and keeps each VM's state.</summary>
internal sealed class FakeHyperVHost : IHyperVHost
{
    private readonly Dictionary<string, HyperVVmState> _vms = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    public HyperVVmDefinition? Created { get; private set; }

    public string? ParentDisk { get; private set; }

    public Exception? FailCreateVm { get; set; }

    public void Set(string name, HyperVVmState state) => _vms[name] = state;

    public Task<string> CreateDifferencingDiskAsync(string vmName, string parentDiskPath, CancellationToken ct)
    {
        Calls.Add($"disk:{vmName}");
        ParentDisk = parentDiskPath;
        return Task.FromResult($"vms/{vmName}/disk.vhdx");
    }

    public Task CreateVmAsync(HyperVVmDefinition vm, CancellationToken ct)
    {
        Calls.Add($"create:{vm.Name}");
        if (FailCreateVm is not null)
        {
            throw FailCreateVm;
        }

        Created = vm;
        _vms[vm.Name] = HyperVVmState.Off;
        return Task.CompletedTask;
    }

    public Task StartAsync(string vmName, CancellationToken ct) => Move("start", vmName, HyperVVmState.Running);

    public Task StopAsync(string vmName, bool force, CancellationToken ct) => Move(force ? "stop-force" : "stop", vmName, HyperVVmState.Off);

    public Task SaveAsync(string vmName, CancellationToken ct) => Move("save", vmName, HyperVVmState.Saved);

    public Task CheckpointAsync(string vmName, string checkpointName, CancellationToken ct)
    {
        Calls.Add($"checkpoint:{vmName}:{checkpointName}");
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string vmName, string checkpointName, CancellationToken ct)
    {
        Calls.Add($"restore:{vmName}:{checkpointName}");
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string vmName, bool deleteDisk, CancellationToken ct)
    {
        Calls.Add($"remove:{vmName}:{deleteDisk}");
        _vms.Remove(vmName);
        return Task.CompletedTask;
    }

    public Task<HyperVVmState> GetStateAsync(string vmName, CancellationToken ct) =>
        Task.FromResult(_vms.TryGetValue(vmName, out var state) ? state : HyperVVmState.Missing);

    private Task Move(string call, string vmName, HyperVVmState next)
    {
        Calls.Add($"{call}:{vmName}");
        _vms[vmName] = next;
        return Task.CompletedTask;
    }
}

internal sealed class FixedParentResolver(string directory) : IHyperVParentResolver
{
    public string ResolveParentDisk(Bazaar.Server.Vms.Lifecycle.ImageVersionId parent) => Path.Combine(directory, parent.Value.ToString("N") + ".vhdx");
}

internal sealed class FixedHostFacts(Bazaar.Contracts.Modules.HostFacts facts) : Bazaar.Contracts.Modules.IHostFactsSource
{
    public Bazaar.Contracts.Modules.HostFacts Current() => facts;

    public static Bazaar.Contracts.Modules.HostFacts WindowsWithHyperV() =>
        new("windows", "x64", new HashSet<string>(["hyperv"], StringComparer.Ordinal));
}
