using Bazaar.Server.Agents.Data;
using Bazaar.Server.Setup;
using Npgsql;

namespace Bazaar.Server.Company;

/// <summary>
/// The Postgres-backed <see cref="ISetupCompanySeeder"/>: it writes <c>company_profile</c> (company 0001), the owner <c>human</c> row
/// (remote 0001) and the first <c>agent</c> rows through <see cref="AgentRepository"/>, so an agent gets its mention handle the way
/// every other agent does. Agents start in the <c>created</c> state; nothing here activates one.
/// </summary>
/// <remarks>
/// The profile and owner rows are written in one transaction under one advisory lock. The agents are created before that transaction
/// commits (the repository uses its own connections); if an agent write or the commit fails, the agents created so far are removed
/// and the failure is rethrown, so a failed seed leaves nothing. A company with another slug or other values, or an owner with
/// another display name, is refused, never overwritten. A seeded agent is found by its name (case-insensitive, not retired).
/// Rollback removes only rows the outcome names, and only while they are unchanged: an agent that left <c>created</c>, or a row a
/// foreign key now points at, stays (it is reported by the audit row, and removing it is the operator's call).
/// </remarks>
public sealed class PostgresCompanySeeder(NpgsqlDataSource dataSource, AgentRepository agents) : ISetupCompanySeeder
{
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended('setup:company', 0))";
    private const string _profileSql = "SELECT name, slug, locale, timezone, owner_second_factor_required FROM company_profile WHERE id = 1";
    private const string _insertProfileSql = "INSERT INTO company_profile (id, name, slug, locale, timezone, owner_second_factor_required) VALUES (1, @name, @slug, @locale, @timezone, @second)";
    private const string _ownerSql = "SELECT id, display_name FROM human WHERE role = 'owner'";
    private const string _insertOwnerSql = "INSERT INTO human (id, display_name, role, status) VALUES (@id, @name, 'owner', 'active') ON CONFLICT DO NOTHING";
    private const string _findAgentSql = "SELECT agent_id, name FROM agent WHERE lower(name) = lower(@name) AND state <> 'retired' ORDER BY created_at LIMIT 1";
    private const string _deleteHandleSql = "DELETE FROM principal_handle WHERE principal_id = @id AND principal_kind = 'agent'";
    private const string _deleteAgentSql = "DELETE FROM agent WHERE agent_id = @id AND state = 'created'";
    private const string _deleteOwnerSql = "DELETE FROM human WHERE id = @id AND role = 'owner'";
    private const string _deleteProfileSql = "DELETE FROM company_profile WHERE id = 1 AND slug = @slug";

    // A plain foreign key raises foreign_key_violation (23503), a RESTRICT one restrict_violation (23001). An undefined table is 42P01.
    private const string _foreignKeyViolation = "23503";
    private const string _restrictViolation = "23001";
    private const string _undefinedTable = "42P01";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly AgentRepository _agents = agents ?? throw new ArgumentNullException(nameof(agents));

    /// <inheritdoc />
    public async Task<CompanySeedPreview> PreviewAsync(CompanySeedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var profile = await ReadProfileAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var owner = await ReadOwnerAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var conflict = ConflictOf(request, profile, owner);
        var toCreate = new List<string>();
        var existing = new List<string>();
        foreach (var name in request.AgentNames)
        {
            (await FindAgentAsync(connection, null, name, cancellationToken).ConfigureAwait(false) is null ? toCreate : existing).Add(name);
        }

        return new CompanySeedPreview(profile is not null, owner is not null, toCreate, existing, conflict);
    }

    /// <inheritdoc />
    public async Task<CompanySeedResult> SeedAsync(CompanySeedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var createdAgents = new List<SeededAgent>();
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var locking = new NpgsqlCommand(_lockSql, connection, transaction))
            {
                await locking.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            var profile = await ReadProfileAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var owner = await ReadOwnerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var conflict = ConflictOf(request, profile, owner);
            if (conflict is not null)
            {
                return CompanySeedResult.Refused(conflict);
            }

            var companyCreated = profile is null;
            if (companyCreated)
            {
                await using var insert = new NpgsqlCommand(_insertProfileSql, connection, transaction);
                insert.Parameters.AddWithValue("name", request.Name);
                insert.Parameters.AddWithValue("slug", request.Slug);
                insert.Parameters.AddWithValue("locale", request.Locale);
                insert.Parameters.AddWithValue("timezone", request.Timezone);
                insert.Parameters.AddWithValue("second", request.RequireSecondFactor);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            Guid? ownerCreated = null;
            if (owner is null)
            {
                var id = Guid.CreateVersion7();
                await using var insert = new NpgsqlCommand(_insertOwnerSql, connection, transaction);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("name", request.OwnerDisplayName);
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    // The one-owner unique index fired: another owner appeared between the read and the insert.
                    return CompanySeedResult.Refused("the install already has an owner; setup does not replace an owner.");
                }

                ownerCreated = id;
            }

            var found = new List<SeededAgent>();
            foreach (var name in request.AgentNames)
            {
                var existing = await FindAgentAsync(connection, transaction, name, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    found.Add(existing);
                    continue;
                }

                var agentId = await _agents.CreateAsync(name, null, cancellationToken).ConfigureAwait(false);
                createdAgents.Add(new SeededAgent(agentId, name));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CompanySeedResult(new CompanySeedOutcome(request.Slug, companyCreated, ownerCreated, createdAgents), null, found);
        }
        catch
        {
            // Agents are written outside the transaction (the repository uses its own connections), so a failure must remove them by hand.
            await RemoveAgentsAsync([.. createdAgents], CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CompanySeedOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await RemoveAgentsAsync(outcome.AgentsCreated, cancellationToken).ConfigureAwait(false);
        if (outcome.OwnerCreatedId is null && !outcome.CompanyCreated)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var locking = new NpgsqlCommand(_lockSql, connection, transaction))
        {
            await locking.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (outcome.OwnerCreatedId is { } ownerId)
        {
            await TryDeleteAsync(connection, transaction, _deleteOwnerSql, "id", ownerId, "company_seed_owner", cancellationToken).ConfigureAwait(false);
        }

        if (outcome.CompanyCreated)
        {
            await TryDeleteAsync(connection, transaction, _deleteProfileSql, "slug", outcome.Slug, "company_seed_profile", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ConflictOf(CompanySeedRequest request, ProfileRow? profile, OwnerRow? owner)
    {
        if (profile is not null && profile != new ProfileRow(request.Name, request.Slug, request.Locale, request.Timezone, request.RequireSecondFactor))
        {
            return "the install already has a company with other values; one company per install, and setup does not edit it.";
        }

        if (owner is not null && !string.Equals(owner.DisplayName, request.OwnerDisplayName, StringComparison.Ordinal))
        {
            return "the install already has an owner with another display name; setup does not replace an owner.";
        }

        return null;
    }

    private async Task RemoveAgentsAsync(IReadOnlyList<SeededAgent> seeded, CancellationToken cancellationToken)
    {
        foreach (var agent in seeded.Reverse())
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // The handle goes with the agent, but only when the agent row can go: both deletes sit behind one savepoint.
            await using (var save = new NpgsqlCommand("SAVEPOINT company_seed_agent", connection, transaction))
            {
                await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await ExecuteAsync(connection, transaction, _deleteHandleSql, "id", agent.Id, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, _deleteAgentSql, "id", agent.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState is _foreignKeyViolation or _restrictViolation or _undefinedTable)
            {
                await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT company_seed_agent", connection, transaction);
                await back.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryDeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, string parameter, object value, string savepoint, CancellationToken cancellationToken)
    {
        await using (var save = new NpgsqlCommand("SAVEPOINT " + savepoint, connection, transaction))
        {
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await ExecuteAsync(connection, transaction, sql, parameter, value, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is _foreignKeyViolation or _restrictViolation)
        {
            // A device, credential or other row now hangs off it, so it stays. Removing it is the operator's call.
            await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT " + savepoint, connection, transaction);
            await back.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, string parameter, object value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(parameter, value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProfileRow?> ReadProfileAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_profileSql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProfileRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4))
            : null;
    }

    private static async Task<OwnerRow?> ReadOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_ownerSql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new OwnerRow(reader.GetGuid(0), reader.GetString(1)) : null;
    }

    private static async Task<SeededAgent?> FindAgentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_findAgentSql, connection, transaction);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new SeededAgent(reader.GetGuid(0), reader.GetString(1)) : null;
    }

    private sealed record ProfileRow(string Name, string Slug, string Locale, string Timezone, bool SecondFactorRequired);

    private sealed record OwnerRow(Guid Id, string DisplayName);
}
