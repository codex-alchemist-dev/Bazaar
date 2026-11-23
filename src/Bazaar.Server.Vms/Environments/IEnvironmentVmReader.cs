namespace Bazaar.Server.Vms.Environments;

/// <summary>
/// The two reads the cross-module directory needs from <c>environment_vm</c>. <see cref="EnvironmentRepository"/> is the production
/// source; the seam lets the mapping and filtering be tested without a database.
/// </summary>
public interface IEnvironmentVmReader
{
    /// <summary>Lists every VM of one agent, retired ones included, oldest first.</summary>
    Task<IReadOnlyList<EnvironmentVmRecord>> ListVmsForAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Lists every VM recorded as Running, of any agent, oldest first.</summary>
    Task<IReadOnlyList<EnvironmentVmRecord>> ListRunningVmsAsync(CancellationToken ct = default);
}
