using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Ports;

/// <summary>
/// The production <see cref="IAgentVmDirectory"/>: reads <c>environment_vm</c> through <see cref="IEnvironmentVmReader"/> and
/// maps the rows. It calls no backend. A read failure propagates; callers decide how to fail.
/// </summary>
public sealed class EnvironmentAgentVmDirectory(IEnvironmentVmReader reader) : IAgentVmDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct)
    {
        var rows = await reader.ListVmsForAgentAsync(agentId, ct).ConfigureAwait(false);
        return Map(rows);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct)
    {
        var rows = await reader.ListRunningVmsAsync(ct).ConfigureAwait(false);
        // Only rows that map to Running: a Running row with no backend reference is reported by ListForAgentAsync as Other and is not listed here.
        return Map(rows).Where(v => v.State == VmRunState.Running).ToList();
    }

    private static List<AgentVmInfo> Map(IReadOnlyList<EnvironmentVmRecord> rows)
    {
        var result = new List<AgentVmInfo>(rows.Count);
        foreach (var row in rows)
        {
            if (AgentVmMapping.ToInfo(row) is { } info)
                result.Add(info);
        }

        return result;
    }
}
