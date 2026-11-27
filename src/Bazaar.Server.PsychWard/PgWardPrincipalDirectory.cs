using Bazaar.Contracts.Gc;
using Bazaar.Server.PsychWard.Abstractions;
using Npgsql;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// The <see cref="IWardPrincipalDirectory"/> over Postgres: agent names come from the runtime's <c>agent</c> table (the agent id is
/// its principal id in GCs); the owner comes from <see cref="IWardOwnerSource"/>, which identity supplies.
/// </summary>
public sealed class PgWardPrincipalDirectory(NpgsqlDataSource dataSource, IWardOwnerSource owners) : IWardPrincipalDirectory
{
    /// <inheritdoc />
    public Task<GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken) => owners.GetOwnerAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<GcPrincipal?> GetAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT name FROM agent WHERE agent_id = @id");
        cmd.Parameters.AddWithValue("id", agentId);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string name
            ? new GcPrincipal(agentId, PrincipalKind.Agent, name)
            : null;
    }
}
