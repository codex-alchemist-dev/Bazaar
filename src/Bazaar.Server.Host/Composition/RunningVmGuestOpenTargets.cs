using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.Host.Composition;

/// <summary>The VM rows the open targets need. A seam over <see cref="EnvironmentRepository"/> so the mapping is tested without a database.</summary>
internal interface IRunningVmSource
{
    /// <summary>Every VM that is not retired.</summary>
    Task<IReadOnlyList<EnvironmentVmRecord>> ListNonRetiredAsync(CancellationToken ct);

    /// <summary>One VM, or null.</summary>
    Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct);
}

/// <summary><see cref="IRunningVmSource"/> over the Vms environment repository.</summary>
internal sealed class RepositoryRunningVmSource(EnvironmentRepository repository) : IRunningVmSource
{
    public Task<IReadOnlyList<EnvironmentVmRecord>> ListNonRetiredAsync(CancellationToken ct) => repository.ListNonRetiredVmsAsync(ct);

    public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct) => repository.GetVmAsync(vmId, ct);
}

/// <summary>
/// <see cref="IGuestOpenTargets"/> over the VM store (doc 16 "Channel opener", PROPOSED). A VM is a target only when its state is
/// <see cref="VmState.Running"/>, it has a backend ref that passes the strict VM-name pattern, and <see cref="IGuestNonceSource"/> has a nonce for it.
/// Anything else is left out, so the opener never waits on a VM it could not verify.
/// </summary>
internal sealed class RunningVmGuestOpenTargets(IRunningVmSource vms, IGuestNonceSource nonces) : IGuestOpenTargets
{
    public async ValueTask<IReadOnlyList<GuestOpenTarget>> ListRunningAsync(CancellationToken ct)
    {
        var records = await vms.ListNonRetiredAsync(ct).ConfigureAwait(false);
        var targets = new List<GuestOpenTarget>();
        foreach (var record in records)
        {
            if (record.State == VmState.Running && await MapAsync(record, ct).ConfigureAwait(false) is { } target)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    public async ValueTask<GuestOpenTarget?> GetAsync(Guid vmId, CancellationToken ct)
    {
        var record = await vms.GetAsync(vmId, ct).ConfigureAwait(false);
        return record is { State: VmState.Running } ? await MapAsync(record, ct).ConfigureAwait(false) : null;
    }

    private async ValueTask<GuestOpenTarget?> MapAsync(EnvironmentVmRecord record, CancellationToken ct)
    {
        if (!GuestChannelRegistry.IsValidVmName(record.BackendRef))
        {
            return null;
        }

        string? nonce;
        try
        {
            nonce = await nonces.GetAsync(record.Id, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // One VM whose nonce cannot be read must not hide the others. It has no target until it can be.
            return null;
        }

        return string.IsNullOrEmpty(nonce)
            ? null
            : new GuestOpenTarget(
                record.Id,
                record.AgentId.ToString("D"),
                record.BackendRef!,
                record.Os == VmOs.Windows ? GuestOs.Windows : GuestOs.Linux,
                nonce);
    }
}
