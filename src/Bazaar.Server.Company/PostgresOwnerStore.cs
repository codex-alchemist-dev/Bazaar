using Npgsql;

namespace Bazaar.Server.Company;

/// <summary>
/// The Postgres <see cref="IOwnerStore"/>: the owner <c>human</c> row (remote 0001, role owner, status active) and the
/// <c>owner_profile</c> row (company 0001), in one transaction under one advisory lock. An existing owner with another display name,
/// or recorded choices that differ, are refused. Rollback removes only rows the outcome names, and leaves a row a foreign key now
/// points at (a device or credential), which is the operator's call.
/// </summary>
public sealed class PostgresOwnerStore(NpgsqlDataSource dataSource) : IOwnerStore
{
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended('setup:owner', 0))";
    private const string _ownerSql = "SELECT display_name FROM human WHERE role = 'owner'";
    private const string _insertOwnerSql = "INSERT INTO human (id, display_name, role, status) VALUES (@id, @name, 'owner', 'active') ON CONFLICT DO NOTHING";
    private const string _choicesSql = "SELECT second_factor_required, recovery_bundle_ref, recovery_printed_codes FROM owner_profile WHERE id = 1";
    private const string _insertChoicesSql = "INSERT INTO owner_profile (id, second_factor_required, recovery_bundle_ref, recovery_printed_codes) VALUES (1, @second, @bundle, @printed)";
    private const string _deleteOwnerSql = "DELETE FROM human WHERE id = @id AND role = 'owner'";
    private const string _deleteChoicesSql = "DELETE FROM owner_profile WHERE id = 1";

    // A plain foreign key raises foreign_key_violation (23503), a RESTRICT one restrict_violation (23001).
    private const string _foreignKeyViolation = "23503";
    private const string _restrictViolation = "23001";

    /// <inheritdoc />
    public async Task<OwnerResult> EnsureAsync(OwnerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var locking = new NpgsqlCommand(_lockSql, connection, transaction))
        {
            await locking.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        string? existingName = null;
        await using (var read = new NpgsqlCommand(_ownerSql, connection, transaction))
        {
            existingName = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (existingName is not null && !string.Equals(existingName, request.DisplayName, StringComparison.Ordinal))
        {
            return OwnerResult.Refused("the install already has an owner with another display name; setup does not replace an owner.");
        }

        (bool Second, string? Bundle, bool Printed)? choices = null;
        await using (var read = new NpgsqlCommand(_choicesSql, connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                choices = (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2));
            }
        }

        if (choices is { } held && held != (request.RequireSecondFactor, request.RecoveryBundleRef, request.PrintedCodes))
        {
            return OwnerResult.Refused("the install already recorded other owner choices; setup does not edit them.");
        }

        Guid? ownerCreated = null;
        if (existingName is null)
        {
            var id = Guid.CreateVersion7();
            await using var insert = new NpgsqlCommand(_insertOwnerSql, connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("name", request.DisplayName);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                // The one-owner unique index fired: another owner appeared between the read and the insert.
                return OwnerResult.Refused("the install already has an owner; setup does not replace an owner.");
            }

            ownerCreated = id;
        }

        var choicesCreated = choices is null;
        if (choicesCreated)
        {
            await using var insert = new NpgsqlCommand(_insertChoicesSql, connection, transaction);
            insert.Parameters.AddWithValue("second", request.RequireSecondFactor);
            insert.Parameters.AddWithValue("bundle", (object?)request.RecoveryBundleRef ?? DBNull.Value);
            insert.Parameters.AddWithValue("printed", request.PrintedCodes);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OwnerResult(new OwnerOutcome(ownerCreated, choicesCreated), null);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(OwnerOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.IsEmpty)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var locking = new NpgsqlCommand(_lockSql, connection, transaction))
        {
            await locking.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (outcome.ChoicesCreated)
        {
            await using var delete = new NpgsqlCommand(_deleteChoicesSql, connection, transaction);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (outcome.OwnerCreatedId is { } ownerId)
        {
            await using (var save = new NpgsqlCommand("SAVEPOINT owner_seed", connection, transaction))
            {
                await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await using var delete = new NpgsqlCommand(_deleteOwnerSql, connection, transaction);
                delete.Parameters.AddWithValue("id", ownerId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState is _foreignKeyViolation or _restrictViolation)
            {
                // A device, credential or other row now hangs off the owner, so it stays.
                await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT owner_seed", connection, transaction);
                await back.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
