using System.Collections.Concurrent;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// The setup step that stores the plan's model sources in the gateway's provider table (doc 45 step 9; doc 05 <c>gw_provider</c>).
/// A plan source is only <c>{id, keyRef}</c>, so the kind, adapter, base URL and credential kind come from the source catalogue
/// (<see cref="IModelSourceCatalogue"/>, default <see cref="ModelSourceCatalogue.Default"/>), never from a guess. A source the catalogue
/// does not know, a key where the source takes none, or no key where it needs one fails the step before anything is written.
/// </summary>
/// <remarks>
/// The default routing preset is a registry setting (<c>gateway.default_preset</c>) written by <see cref="InstallSettingsStep"/> from the
/// same plan; this step covers the <c>models</c> section together with it, so a host registers both (the composition roots do).
/// A key reference is stored as the plan wrote it: the table holds a reference only and its CHECK refuses anything outside
/// <c>secret://bazaar/providers/</c>. A subscription or OAuth login cannot be stored: the credential kinds are api_key, bearer and none.
/// Terms acknowledgement (<c>terms_ack_at</c>), extra headers, an outbound proxy and the enabled flag are the operator's and are never
/// touched on an existing row. All rows are written in one transaction under one advisory lock. The step is idempotent (a row that
/// already holds the planned values is left alone). Rollback removes a row this run created (when no model row hangs off it) and
/// restores the five columns this run changed on an existing row, but only while the row still holds what this run wrote.
/// No key reference or value is put in a message.
/// </remarks>
public sealed class GatewaySourcesStep(NpgsqlDataSource dataSource, IModelSourceCatalogue? catalogue = null) : ISetupStep
{
    /// <summary>The step id written to the audit row.</summary>
    public const string StepId = "models.sources";

    /// <summary>Doc 45 step 9. After the data root, custody and owner steps and before the git, email and company steps, in the 100s of the doc 45 numbering.</summary>
    public const int StepOrder = 900;

    /// <summary>The plan section this step applies. The preset half is written by <see cref="InstallSettingsStep"/>.</summary>
    public static readonly IReadOnlyList<string> CoveredSections = ["models"];

    private const string _selectSql = "SELECT kind, adapter, base_url, auth_kind, secret_ref FROM gw_provider WHERE id = @id";

    private const string _insertSql = """
        INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind, secret_ref, headers, enabled)
        VALUES (@id, @kind, @adapter, @base_url, @auth_kind, @secret_ref, '{}'::jsonb, true)
        """;

    private const string _updateSql = "UPDATE gw_provider SET kind = @kind, adapter = @adapter, base_url = @base_url, auth_kind = @auth_kind, secret_ref = @secret_ref WHERE id = @id";

    private const string _deleteSql = "DELETE FROM gw_provider WHERE id = @id";

    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended('setup:gw_provider', 0))";

    // A RESTRICT foreign key raises restrict_violation (23001), a plain one foreign_key_violation (23503). gw_model uses RESTRICT.
    private const string _foreignKeyViolation = "23503";
    private const string _restrictViolation = "23001";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly IModelSourceCatalogue _catalogue = catalogue ?? ModelSourceCatalogue.Default;

    // What this run changed, by plan hash, for the rollback of the same apply. An apply holds the instance lock, so there is one at a time.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Change>> _changed = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers => CoveredSections;

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryResolve(plan, out var desired, out var failure))
        {
            return new SetupStepResult(false, failure);
        }

        var changed = new List<Change>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var row in desired)
        {
            var stored = await ReadAsync(connection, transaction, row.Id, cancellationToken).ConfigureAwait(false);
            if (stored == row)
            {
                continue;
            }

            if (stored is null)
            {
                await WriteAsync(_insertSql, connection, transaction, row, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(_updateSql, connection, transaction, row, cancellationToken).ConfigureAwait(false);
            }

            changed.Add(new Change(row.Id, stored, row));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _changed[context.PlanHash] = changed;
        return new SetupStepResult(true, $"{changed.Count} of {desired.Count} model sources changed.");
    }

    /// <inheritdoc />
    public async Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_changed.TryRemove(context.PlanHash, out var changed) || changed.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        for (var index = changed.Count - 1; index >= 0; index--)
        {
            var change = changed[index];
            var current = await ReadAsync(connection, transaction, change.Id, cancellationToken).ConfigureAwait(false);
            if (current != change.Written)
            {
                // Someone changed the row after this run wrote it. That newer state is not ours to undo.
                continue;
            }

            if (change.Before is null)
            {
                await using var savepoint = new NpgsqlCommand("SAVEPOINT gw_provider_delete", connection, transaction);
                await savepoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var delete = new NpgsqlCommand(_deleteSql, connection, transaction);
                    delete.Parameters.AddWithValue("id", change.Id);
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException exception) when (exception.SqlState is _foreignKeyViolation or _restrictViolation)
                {
                    // A model or usage row now hangs off this provider, so the row stays. Disabling it is the operator's call.
                    await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT gw_provider_delete", connection, transaction);
                    await back.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await WriteAsync(_updateSql, connection, transaction, change.Before, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves every plan source through the catalogue. Fails with a message that names source ids only, never a reference.</summary>
    private bool TryResolve(SetupPlan plan, out List<ProviderRow> desired, out string? failure)
    {
        desired = [];
        failure = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in plan.Models.Sources)
        {
            if (!_catalogue.TryGet(source.Id, out var entry) || entry is null)
            {
                failure = $"model source '{source.Id}' is not in the source catalogue, so its base URL is unknown.";
                return false;
            }

            if (!seen.Add(entry.Id))
            {
                failure = $"model source '{source.Id}' names a provider another entry already names.";
                return false;
            }

            if (entry.TakesKey && (string.IsNullOrEmpty(source.KeyRef) || !source.KeyRef.StartsWith(_keyPrefix, StringComparison.Ordinal) || source.KeyRef.Length == _keyPrefix.Length))
            {
                failure = $"model source '{source.Id}' needs a secret://bazaar/providers/ key reference.";
                return false;
            }

            if (!entry.TakesKey && source.KeyRef is not null)
            {
                failure = $"model source '{source.Id}' is a local runtime and takes no key.";
                return false;
            }

            desired.Add(new ProviderRow(entry.Id, entry.Kind, entry.Adapter, entry.BaseUrl, entry.AuthKind, entry.TakesKey ? source.KeyRef : null));
        }

        return true;
    }

    private const string _keyPrefix = "secret://bazaar/providers/";

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_lockSql, connection, transaction);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProviderRow?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_selectSql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProviderRow(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task WriteAsync(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, ProviderRow row, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("kind", row.Kind);
        command.Parameters.AddWithValue("adapter", row.Adapter);
        command.Parameters.AddWithValue("base_url", row.BaseUrl);
        command.Parameters.AddWithValue("auth_kind", row.AuthKind);
        command.Parameters.AddWithValue("secret_ref", (object?)row.SecretRef ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record ProviderRow(string Id, string Kind, string Adapter, string BaseUrl, string AuthKind, string? SecretRef);

    private sealed record Change(string Id, ProviderRow? Before, ProviderRow Written);
}
