using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.Vms.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools.Vms;

/// <summary>
/// Maps a calling agent to its own VM display target (docs/design/59, section 4) over the VM module's read port. Only a VM that is
/// recorded as running, belongs to that agent, has the requested guest OS and has a backend reference is a target. A saved, stopped,
/// checkpointed or retired VM is not. The browser kind is not a VM and gets no target here.
/// </summary>
/// <remarks>
/// Every failure gives no target: no directory, a read that throws, a read that takes longer than
/// <see cref="AgentTargetsOptions.ReadTimeoutSeconds"/>, or an entry that does not match. Cancellation by the caller is not a verdict
/// and propagates. The adapter never starts a VM and never reads anything but the directory.
/// </remarks>
public sealed class VmAgentDisplayTargets : IAgentDisplayTargets
{
    private readonly IAgentVmDirectory? _directory;
    private readonly TimeProvider _time;
    private readonly AgentTargetsOptions _options;

    /// <summary>Creates the adapter. A <see langword="null"/> directory gives no target for any agent.</summary>
    public VmAgentDisplayTargets(IAgentVmDirectory? directory, TimeProvider time, AgentTargetsOptions options)
    {
        _directory = directory;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<DisplayTarget?> ResolveAsync(Guid agentId, AgentDisplayKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VmGuestOs? wanted = kind switch
        {
            AgentDisplayKind.Linux => VmGuestOs.Linux,
            AgentDisplayKind.Windows => VmGuestOs.Windows,
            _ => null,
        };
        if (wanted is not { } os || _directory is null || agentId == Guid.Empty)
            return null;

        IReadOnlyList<AgentVmInfo> vms;
        try
        {
            vms = await _directory.ListForAgentAsync(agentId, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(_options.ReadTimeoutSeconds), _time, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A directory failure or a timeout: nothing is known about the agent's VMs, so there is no target.
            return null;
        }

        // The directory is asked for this agent only, but the entry's owner is checked again: a wrong row must never become a target.
        AgentVmInfo? vm = vms.FirstOrDefault(v =>
            v is not null
            && v.AgentId == agentId
            && v.Os == os
            && v.State == VmRunState.Running
            && !string.IsNullOrWhiteSpace(v.BackendRef));
        if (vm is null)
            return null;

        return new DisplayTarget(
            agentId.ToString("D"),
            vm.VmId,
            os == VmGuestOs.Windows ? DisplayGuestOs.Windows : DisplayGuestOs.Linux,
            vm.BackendRef!);
    }
}
