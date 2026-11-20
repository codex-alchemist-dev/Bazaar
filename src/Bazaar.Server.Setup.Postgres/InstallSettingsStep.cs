using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// The setup step that writes the plan's install-scope settings (doc 45 sections 1.2 and 1.3; doc 03 settings registry, doc 04
/// <c>setting</c> table). The settings are the plan's <c>settings</c> map and the four plan fields that map to one registry key each
/// (see <see cref="SetupSettingProjection"/>). It covers the settings part of doc 45 steps 1 (release channel), 5 (the identity KEK provider, when set), 2 (the security half of the update policy), 12 (scam-mail
/// protocol) and 15 (status bind and port).
/// </summary>
/// <remarks>
/// Every value goes through the registry first (<see cref="ISetupSettingDefinition.Evaluate"/>), and only an accepted value is written; a
/// held or rejected value fails the step, so nothing the registry would refuse is stored. All keys are written in one transaction
/// under the same per-key advisory locks as the server's settings store, so a failure writes nothing. The step is idempotent: a key
/// that already holds the planned value is left alone, so a second apply changes nothing. The audit rows (<c>setting.changed</c>) are
/// the applier's, written once for the whole apply, so this step writes none. A value is never put in an error or a message.
/// Rollback restores each key this run changed to what it held before (or removes it when it had no row), but only while the stored
/// value is still the one this run wrote.
/// </remarks>
public sealed class InstallSettingsStep(NpgsqlDataSource dataSource, ISetupSettingCatalog catalog) : ISetupStep
{
    /// <summary>The step id written to the audit row.</summary>
    public const string StepId = "install.settings";

    /// <summary>Step 15 of doc 45 section 1.3 (status and network) is the last configuration step, so this runs after the ones before it.</summary>
    public const int StepOrder = 1500;

    /// <summary>The plan sections this step applies: the settings-backed parts of the plan (doc 45 steps 1, 2, 12 and 15, plus the settings map).</summary>
    public static readonly IReadOnlyList<string> CoveredSections = ["install.releaseChannel", "install.updatePolicy", "scamMailProtocol", "network", "custody.identityKek", "settings"];

    private const string _selectSql = "SELECT value::text, updated_by FROM setting WHERE key = @key AND scope = 'install' AND scope_id = ''";

    private const string _upsertSql = """
        INSERT INTO setting (key, scope, scope_id, value, updated_at, updated_by)
        VALUES (@key, 'install', '', @value::jsonb, now(), @updated_by)
        ON CONFLICT (key, scope, scope_id)
        DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
        """;

    private const string _deleteSql = "DELETE FROM setting WHERE key = @key AND scope = 'install' AND scope_id = ''";

    // The same lock key text as the server's settings store, so a setup write and a settings write to one key take turns.
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly ISetupSettingCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

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
        foreach (var (key, valueJson) in desired)
        {
            await LockAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
            var stored = await ReadAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
            if (stored is not null && SameJson(stored.ValueJson, valueJson))
            {
                continue;
            }

            await UpsertAsync(connection, transaction, key, valueJson, context.OperatorIdentity, cancellationToken).ConfigureAwait(false);
            changed.Add(new Change(key, stored, valueJson));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _changed[context.PlanHash] = changed;
        return new SetupStepResult(true, $"{changed.Count} of {desired.Count} settings changed.");
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
        for (var index = changed.Count - 1; index >= 0; index--)
        {
            var change = changed[index];
            await LockAsync(connection, transaction, change.Key, cancellationToken).ConfigureAwait(false);
            var current = await ReadAsync(connection, transaction, change.Key, cancellationToken).ConfigureAwait(false);
            if (current is null || !SameJson(current.ValueJson, change.WrittenJson))
            {
                // Someone changed the key after this run wrote it. That newer value is not ours to undo.
                continue;
            }

            if (change.Before is null)
            {
                await DeleteAsync(connection, transaction, change.Key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UpsertAsync(connection, transaction, change.Key, change.Before.ValueJson, change.Before.UpdatedBy, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs every projected value through the registry. Fails with a message that names keys only, never values.</summary>
    private bool TryResolve(SetupPlan plan, out List<(string Key, string ValueJson)> desired, out string? failure)
    {
        desired = [];
        failure = null;
        if (!SetupSettingProjection.IsOtherUpdatePolicyBacked(plan))
        {
            // Defence in depth: the validator already refuses this, but a step must not report success for what it cannot store.
            failure = "'install.updatePolicy.other' can only be manual: no setting stores an automatic choice.";
            return false;
        }

        foreach (var setting in SetupSettingProjection.Project(plan))
        {
            if (!_catalog.TryGet(setting.Key, out var definition) || definition is null || !definition.IsInstallScope)
            {
                failure = $"'{setting.Key}' is not a registered install setting.";
                return false;
            }

            var evaluation = definition.Evaluate(SetupConstants.PlanActorId, setting.Value);
            if (!evaluation.Accepted)
            {
                failure = $"'{setting.Key}' was not accepted by the settings registry.";
                return false;
            }

            desired.Add((setting.Key, evaluation.ValueJson));
        }

        return true;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_lockSql, connection, transaction);
        command.Parameters.AddWithValue("lock_key", $"setting:install::{key}");
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stored?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_selectSql, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new Stored(reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task UpsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, string valueJson, string updatedBy, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_upsertSql, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", valueJson);
        command.Parameters.AddWithValue("updated_by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_deleteSql, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SameJson(string left, string right) => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));

    private sealed record Stored(string ValueJson, string UpdatedBy);

    private sealed record Change(string Key, Stored? Before, string WrittenJson);
}
