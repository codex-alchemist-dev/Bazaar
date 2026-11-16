using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// The real <see cref="IVmPauser"/>, over the Vms lifecycle contract. Pause force-stops every running VM of the agent, so the agent
/// cannot act; resume starts every stopped VM of the agent. Both are idempotent: a VM already in the target state is skipped.
/// </summary>
/// <remarks>
/// The Windows Sandbox backend cannot save VM state, so pause is a power cut and resume boots the VM fresh. A backend that can save
/// state (Hyper-V) keeps its state across a pause only if this adapter is changed to save instead; that is a documented choice, not
/// an accident. Any refused lifecycle step throws, so containment fails visibly and the stop stays in force.
/// </remarks>
public sealed class VmContainmentPauser(EnvironmentRepository environments, VmLifecycleService lifecycle) : IVmPauser
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is not an agent id the Vms module knows.</exception>
    /// <exception cref="InvalidOperationException">A VM could not be stopped.</exception>
    public async Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        foreach (var vm in await VmsOf(agentId, ct).ConfigureAwait(false))
        {
            if (vm.State != VmState.Running)
            {
                continue;
            }

            var result = await lifecycle.StopAsync(vm.Id, StopMode.Force, ct).ConfigureAwait(false);
            if (result is VmOperationResult.Refused refused)
            {
                throw new InvalidOperationException($"VM {vm.Id} could not be paused: {refused.Refusal.Reason}.");
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is not an agent id the Vms module knows.</exception>
    /// <exception cref="InvalidOperationException">A VM could not be started.</exception>
    public async Task ResumeAsync(string agentId, CancellationToken ct = default)
    {
        foreach (var vm in await VmsOf(agentId, ct).ConfigureAwait(false))
        {
            if (vm.State != VmState.Stopped)
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
    private async Task<IReadOnlyList<EnvironmentVmRecord>> VmsOf(string agentId, CancellationToken ct)
    {
        if (!Guid.TryParse(agentId, out var agent))
        {
            throw new ArgumentException("The agent id is not a GUID, so its VMs cannot be found.", nameof(agentId));
        }

        var all = await environments.ListVmsForAgentAsync(agent, ct).ConfigureAwait(false);
        return all.Where(vm => vm.State != VmState.Retired).ToList();
    }
}
