using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>The Constitution commit that is current right now, or null when none has been accepted into the Vault yet (doc 24).</summary>
public interface ICurrentConstitutionCommit
{
    /// <summary>The 40-character lowercase commit SHA, or null.</summary>
    Task<string?> GetCurrentCommitShaAsync(CancellationToken cancellationToken = default);
}

/// <summary>The default source: no commit, so no acceptance and the default agent waits (doc 45 section 1.15). Replaced when the Vault supplies one.</summary>
public sealed class NoCurrentConstitutionCommit : ICurrentConstitutionCommit
{
    /// <inheritdoc />
    public Task<string?> GetCurrentCommitShaAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

/// <summary>Records the owner's acceptance of the current Constitution commit. Never inferred: it is only called for the owner's own tick.</summary>
public sealed class PostgresConstitutionAcceptance(NpgsqlDataSource dataSource, ICurrentConstitutionCommit commits) : IConstitutionAcceptance
{
    /// <inheritdoc />
    public async Task<bool> RecordOwnerAcceptanceAsync(Guid ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        if (await commits.GetCurrentCommitShaAsync(cancellationToken).ConfigureAwait(false) is not { } sha) return false;

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO firstrun_constitution_acceptance (owner_id, commit_sha) VALUES ($1, $2) ON CONFLICT (owner_id, commit_sha) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ownerPrincipalId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sha });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
