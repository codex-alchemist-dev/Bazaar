using System.Text.RegularExpressions;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>
/// The <see cref="IEgressTargetResolver"/> over the VM store port (<see cref="IAgentVmLookup"/>). It answers from the store, not from the channel
/// directory: enforcement must cover a VM whose guest channel is down, and a down channel is exactly when a guest might be misbehaving.
/// </summary>
/// <remarks>
/// <para>
/// VM names: every live VM of the agent that has a backend name (retired VMs and VMs not created yet are skipped; their adapters do not exist).
/// A live VM whose name is not a plain name is an error, not a skip, so the enforcer never reports egress in place while a VM is left out.
/// </para>
/// <para>
/// Guest interface: the VM store does not record one today, so <see cref="AgentVmInfo.GuestInterface"/> is null and the target's interface is
/// null; <c>NftablesEgressEnforcer</c> then refuses (fail closed). The Hyper-V enforcer uses names only. Two Linux VMs with different interfaces
/// is an error, because the target holds one.
/// </para>
/// </remarks>
public sealed partial class LookupEgressTargetResolver : IEgressTargetResolver
{
    private readonly IAgentVmLookup _vms;

    /// <summary>Creates the resolver.</summary>
    public LookupEgressTargetResolver(IAgentVmLookup vms)
    {
        ArgumentNullException.ThrowIfNull(vms);
        _vms = vms;
    }

    /// <inheritdoc />
    public async Task<EgressTarget> ResolveAsync(string agentId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(agentId, out var agent) || agent == Guid.Empty)
        {
            throw new EgressEnforcementException("The agent id is not a GUID, so it has no guest.");
        }

        var all = await _vms.ListAsync(agent, ct).ConfigureAwait(false);
        var names = new List<string>();
        var interfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vm in all)
        {
            if (vm.Retired || vm.VmName is null)
            {
                continue;
            }

            if (!NamePattern().IsMatch(vm.VmName))
            {
                throw new EgressEnforcementException("A VM of the agent has a name that is not a plain VM name.");
            }

            names.Add(vm.VmName);
            if (vm.Os == GuestOs.Linux && !string.IsNullOrEmpty(vm.GuestInterface))
            {
                interfaces.Add(vm.GuestInterface);
            }
        }

        if (names.Count == 0)
        {
            throw new EgressEnforcementException("The agent has no guest VM to enforce egress on.");
        }

        if (interfaces.Count > 1)
        {
            throw new EgressEnforcementException("The agent's Linux VMs name more than one guest interface.");
        }

        names.Sort(StringComparer.Ordinal);
        return new EgressTarget(names, interfaces.FirstOrDefault());
    }

    // \A and \z so a trailing newline does not pass. Same shape as the Hyper-V enforcer's VM name rule.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
