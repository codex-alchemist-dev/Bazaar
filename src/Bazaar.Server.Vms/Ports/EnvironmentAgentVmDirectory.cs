using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Ports;

/// <summary>
/// The production <see cref="IAgentVmDirectory"/>: reads <c>environment_vm</c> through <see cref="IEnvironmentVmReader"/> and
/// maps the rows. It calls no backend: the optional backend is asked only for its save capability, a fact about the backend, not a
/// call to it. Memory comes from the row; without a backend the save capability is reported as unknown (null). A read failure
/// propagates; callers decide how to fail.
/// </summary>
public sealed class EnvironmentAgentVmDirectory(IEnvironmentVmReader reader, IVmBackend? backend = null) : IAgentVmDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct)
    {
        var rows = await reader.ListVmsForAgentAsync(agentId, ct).ConfigureAwait(false);
        return Map(rows, backend?.SupportsSaveState);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct)
    {
        var rows = await reader.ListRunningVmsAsync(ct).ConfigureAwait(false);
        // Only rows that map to Running: a Running row with no backend reference is reported by ListForAgentAsync as Other and is not listed here.
        return Map(rows, backend?.SupportsSaveState).Where(v => v.State == VmRunState.Running).ToList();
    }

    private static List<AgentVmInfo> Map(IReadOnlyList<EnvironmentVmRecord> rows, bool? canSaveState)
    {
        var result = new List<AgentVmInfo>(rows.Count);
        foreach (var row in rows)
        {
            if (AgentVmMapping.ToInfo(row, canSaveState) is { } info)
                result.Add(info);
        }

        return result;
    }
}
