namespace Bazaar.Server.Vms.Kvm.Tests;

/// <summary>An in-memory <see cref="IKvmHost"/>. Records every call and keeps each domain's state.</summary>
internal sealed class FakeKvmHost : IKvmHost
{
    private readonly Dictionary<string, KvmDomainState> _domains = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    public KvmDomainDefinition? Defined { get; private set; }

    public string? ParentDisk { get; private set; }

    public Exception? FailDefine { get; set; }

    public void Set(string name, KvmDomainState state) => _domains[name] = state;

    public Task<string> CreateBackedDiskAsync(string domainName, string parentDiskPath, CancellationToken ct)
    {
        Calls.Add($"disk:{domainName}");
        ParentDisk = parentDiskPath;
        return Task.FromResult($"vms/{domainName}/disk.qcow2");
    }

    public Task DefineAsync(KvmDomainDefinition domain, CancellationToken ct)
    {
        Calls.Add($"define:{domain.Name}");
        if (FailDefine is not null)
        {
            throw FailDefine;
        }

        Defined = domain;
        _domains[domain.Name] = KvmDomainState.Off;
        return Task.CompletedTask;
    }

    public Task StartAsync(string domainName, CancellationToken ct) => Move("start", domainName, KvmDomainState.Running);

    public Task StopAsync(string domainName, bool force, CancellationToken ct) => Move(force ? "stop-force" : "stop", domainName, KvmDomainState.Off);

    public Task SaveAsync(string domainName, CancellationToken ct) => Move("save", domainName, KvmDomainState.Saved);

    public Task CheckpointAsync(string domainName, string checkpointName, CancellationToken ct)
    {
        Calls.Add($"checkpoint:{domainName}:{checkpointName}");
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string domainName, string checkpointName, CancellationToken ct)
    {
        Calls.Add($"restore:{domainName}:{checkpointName}");
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string domainName, bool deleteDisk, CancellationToken ct)
    {
        Calls.Add($"remove:{domainName}:{deleteDisk}");
        _domains.Remove(domainName);
        return Task.CompletedTask;
    }

    public Task<KvmDomainState> GetStateAsync(string domainName, CancellationToken ct) =>
        Task.FromResult(_domains.TryGetValue(domainName, out var state) ? state : KvmDomainState.Missing);

    private Task Move(string call, string domainName, KvmDomainState next)
    {
        Calls.Add($"{call}:{domainName}");
        _domains[domainName] = next;
        return Task.CompletedTask;
    }
}

internal sealed class FixedParentResolver(string directory) : IKvmParentResolver
{
    public string ResolveParentDisk(Bazaar.Server.Vms.Lifecycle.ImageVersionId parent) => Path.Combine(directory, parent.Value.ToString("N") + ".qcow2");
}

internal sealed class FixedHostFacts(Bazaar.Contracts.Modules.HostFacts facts) : Bazaar.Contracts.Modules.IHostFactsSource
{
    public Bazaar.Contracts.Modules.HostFacts Current() => facts;

    public static Bazaar.Contracts.Modules.HostFacts LinuxWithKvm() =>
        new("linux", "x64", new HashSet<string>(["kvm"], StringComparer.Ordinal));
}
