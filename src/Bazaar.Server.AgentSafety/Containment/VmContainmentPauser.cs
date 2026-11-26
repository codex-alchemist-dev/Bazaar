using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// The real <see cref="IVmPauser"/>, over the Vms lifecycle contract. Pause stops every running VM of the agent so it cannot act:
/// it saves the state when the VM's own backend supports saving (<see cref="IVmBackend.SupportsSaveState"/>, asked per VM through
/// <see cref="VmLifecycleService.SupportsSaveStateAsync"/>, doc 62 section 9.2), and force-stops otherwise.
/// Resume starts every saved or stopped VM. Both are idempotent: a VM already in the target state is skipped.
/// </summary>
/// <remarks>
/// The choice is made by each backend's capability, not by configuration: Hyper-V keeps VM state across a pause, and the Windows Sandbox
/// backend, which cannot save state, is a power cut that boots fresh on resume. One agent's VMs may sit on different backends. A refused
/// lifecycle step (including a VM no provider serves) throws, so containment fails visibly and the stop stays in force.
/// </remarks>
public sealed class VmContainmentPauser(IAgentVmDirectory vms, VmLifecycleService lifecycle) : IVmPauser
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is not a GUID, so its VMs cannot be found.</exception>
    /// <exception cref="InvalidOperationException">A VM could not be paused.</exception>
    public async Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        foreach (var vm in await LiveVmsAsync(agentId, ct).ConfigureAwait(false))
        {
            if (vm.State != VmState.Running)
            {
                continue;
            }

            var save = await lifecycle.SupportsSaveStateAsync(vm.Id, ct).ConfigureAwait(false) ?? false;
            var result = save
                ? await lifecycle.SaveAsync(vm.Id, ct).ConfigureAwait(false)
                : await lifecycle.StopAsync(vm.Id, StopMode.Force, ct).ConfigureAwait(false);
            if (result is VmOperationResult.Refused refused)
            {
                throw new InvalidOperationException($"VM {vm.Id} could not be paused: {refused.Refusal.Reason}.");
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is not a GUID, so its VMs cannot be found.</exception>
    /// <exception cref="InvalidOperationException">A VM could not be resumed.</exception>
    public async Task ResumeAsync(string agentId, CancellationToken ct = default)
    {
        foreach (var vm in await LiveVmsAsync(agentId, ct).ConfigureAwait(false))
        {
            if (vm.State is not (VmState.Saved or VmState.Stopped))
            {
                continue;
            }

            var result = await lifecycle.StartAsync(vm.Id, ct).ConfigureAwait(false);
            if (result is VmOperationResult.Refused refused)
            {
                throw new InvalidOperationException($"VM {vm.Id} could not be resumed: {refused.Refusal.Reason}.");
            }
        }
    }

    /// <summary>The agent's VMs that are not retired. The Vms module keys VMs by a GUID agent id.</summary>
    private async Task<IReadOnlyList<EnvironmentVmRecord>> LiveVmsAsync(string agentId, CancellationToken ct)
    {
        if (!Guid.TryParse(agentId, out var agent))
        {
            throw new ArgumentException("The agent id is not a GUID, so its VMs cannot be found.", nameof(agentId));
        }

        var all = await vms.ListAsync(agent, ct).ConfigureAwait(false);
        return all.Where(vm => vm.State != VmState.Retired).ToList();
    }
}
