using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// The real <see cref="IVmPauser"/>, over the Vms lifecycle contract. Pause stops every running VM of the agent so it cannot act:
/// it saves the state when the backend supports saving (<see cref="IVmBackend.SupportsSaveState"/>), and force-stops otherwise.
/// A VM in <see cref="VmState.Restored"/> that the backend reports as running is force-stopped. Every VM is tried even when one fails.
/// Resume starts every saved or stopped VM. Both are idempotent: a VM already in the target state is skipped.
/// </summary>
/// <remarks>
/// The choice is made by the backend's capability, not by configuration: Hyper-V keeps VM state across a pause, and the Windows Sandbox
/// backend, which cannot save state, is a power cut that boots fresh on resume. A refused lifecycle step throws, so containment fails
/// visibly and the stop stays in force.
/// </remarks>
public sealed class VmContainmentPauser(IAgentVmDirectory vms, VmLifecycleService lifecycle, IVmBackend backend) : IVmPauser
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is not a GUID, so its VMs cannot be found.</exception>
    /// <exception cref="InvalidOperationException">A VM could not be paused.</exception>
    /// <exception cref="AggregateException">More than one VM could not be paused.</exception>
    public async Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        // Every VM is tried, even after one fails: an agent has two VMs, and a refusal on the first must not leave the second running.
        List<Exception>? failures = null;
        foreach (var vm in await LiveVmsAsync(agentId, ct).ConfigureAwait(false))
        {
            try
            {
                await PauseOneAsync(vm, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is [var only])
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(only).Throw();
        }

        if (failures is not null)
        {
            throw new AggregateException("More than one VM could not be paused.", failures);
        }
    }

    private async Task PauseOneAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        VmOperationResult result;
        if (vm.State == VmState.Running)
        {
            result = backend.SupportsSaveState
                ? await lifecycle.SaveAsync(vm.Id, ct).ConfigureAwait(false)
                : await lifecycle.StopAsync(vm.Id, StopMode.Force, ct).ConfigureAwait(false);
        }
        else if (vm.State == VmState.Restored && vm.BackendRef is { } backendRef)
        {
            // The backend decides the power state after a restore, so a Restored VM may be running. Ask it, and cut the power if so
            // (the transition table allows Restored to Stopped, not to Saved).
            if (await backend.GetStateAsync(new VmHandle(vm.Id, backendRef), ct).ConfigureAwait(false) != VmState.Running)
            {
                return;
            }

            result = await lifecycle.StopAsync(vm.Id, StopMode.Force, ct).ConfigureAwait(false);
        }
        else
        {
            return;
        }

        // A VM that reached a state where it does not run while this call waited (an idle save, a stop) is paused all the same.
        if (result is VmOperationResult.Refused refused
            && refused.Refusal.CurrentState is not (VmState.Saved or VmState.Stopped or VmState.Checkpointed or VmState.Retired or VmState.Absent))
        {
            throw new InvalidOperationException($"VM {vm.Id} could not be paused: {refused.Refusal.Reason}.");
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
