using Npgsql;

namespace Bazaar.Server.GitHub.Tools;

/// <summary>
/// Resolves the GitHub App installation of one agent from its <c>github_app</c> row (doc 21, Identity). A module-local port, so the
/// agent tools take the installation from the server's own mapping and never from model arguments.
/// </summary>
public interface IGitHubAgentInstallations
{
    /// <summary>The installation id of the agent's active App, or null when the agent has none, or it is suspended or revoked.</summary>
    Task<long?> GetInstallationIdAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>Reads <c>github_app</c>; only a row with status <c>active</c> counts.</summary>
public sealed class PgGitHubAgentInstallations(NpgsqlDataSource dataSource) : IGitHubAgentInstallations
{
    private const string _sql = "SELECT installation_id FROM github_app WHERE agent_id = $1 AND status = 'active'";

    /// <inheritdoc />
    public async Task<long?> GetInstallationIdAsync(Guid agentId, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand(_sql);
            cmd.Parameters.AddWithValue(agentId);
            return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }
}

/// <summary>Used when no database exists: no agent has an installation, so every GitHub tool call is refused.</summary>
public sealed class NoGitHubAgentInstallations : IGitHubAgentInstallations
{
    /// <inheritdoc />
    public Task<long?> GetInstallationIdAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult<long?>(null);
}
