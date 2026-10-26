using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Bazaar.Server.Settings;

/// <summary>
/// <see cref="ISettingStore"/> over the <c>setting</c> table (migration 0003), with raw SQL on the shared data source.
/// A write runs in one transaction under a per-key advisory lock. The old value is read under that lock, the audit entry
/// is appended before the row changes, and then the transaction commits. If the audit append throws, nothing is stored.
/// A write that is held or rejected stores nothing, but still writes its audit entry. A write that repeats the stored
/// value is a no-op and writes no audit entry.
/// </summary>
public sealed class NpgsqlSettingStore(NpgsqlDataSource dataSource, ISettingAuditSink audit, SettingRegistry? registry = null) : ISettingStore
{
    private readonly SettingRegistry _registry = registry ?? SettingRegistry.Host;

    private const string _selectSql = "SELECT value::text FROM setting WHERE key = @key AND scope = @scope AND scope_id = @scope_id";

    private const string _upsertSql = """
        INSERT INTO setting (key, scope, scope_id, value, updated_at, updated_by)
        VALUES (@key, @scope, @scope_id, @value::jsonb, now(), @updated_by)
        ON CONFLICT (key, scope, scope_id)
        DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
        """;

    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";

    public async Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(scopeId);
        if (!_registry.Contains(key))
            throw new ArgumentException($"'{key.Name}' is not a registered setting.", nameof(key));
        if (ScopeError(key, scopeId) is { } scopeError)
            throw new ArgumentException(scopeError, nameof(scopeId));

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _selectSql;
        AddKey(cmd, key, scopeId);
        var text = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
            return key.Default;

        using var stored = JsonDocument.Parse(text);
        return key.Read(stored.RootElement);
    }

    public async Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var proposed = JsonSerializer.SerializeToDocument(value, SettingJson.Options);
        return await WriteAsync(key, proposed.RootElement, actor, scopeId, cancellationToken).ConfigureAwait(false);
    }

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_registry.TryGet(key, out var definition) || definition is null)
            return Task.FromResult(new SettingWriteResult(key, SettingWriteOutcome.Rejected, ApprovalTier.None, $"'{key}' is not a registered setting."));

        return WriteAsync(definition, value, actor, scopeId, cancellationToken);
    }

    private async Task<SettingWriteResult> WriteAsync(SettingDefinition definition, JsonElement proposed, SettingActor actor, string scopeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Id);
        ArgumentNullException.ThrowIfNull(scopeId);
        if (!_registry.Contains(definition))
            return new SettingWriteResult(definition.Name, SettingWriteOutcome.Rejected, ApprovalTier.None, $"'{definition.Name}' is not a registered setting.");

        var scopeError = ScopeError(definition, scopeId);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(conn, tx, definition, scopeId, cancellationToken).ConfigureAwait(false);

        var stored = await ReadStoredAsync(conn, tx, definition, scopeId, cancellationToken).ConfigureAwait(false);
        var evaluation = scopeError is null
            ? definition.Evaluate(actor, proposed)
            : new SettingEvaluation(SettingWriteOutcome.Rejected, ApprovalTier.None, scopeError, proposed.GetRawText());
        var result = new SettingWriteResult(definition.Name, evaluation.Outcome, evaluation.ApprovalTier, evaluation.Reason);

        // Repeating the stored value changes nothing, so it writes no audit entry.
        if (evaluation.Outcome == SettingWriteOutcome.Accepted && stored is not null && SameJson(stored, evaluation.ValueJson))
            return result;

        await audit.AppendAsync(
            new SettingChangeAudit(
                definition.Name,
                definition.Scope,
                scopeId,
                actor.Id,
                evaluation.Outcome,
                OldValueJson: stored ?? definition.DefaultValueJson,
                NewValueJson: evaluation.ValueJson,
                evaluation.Reason),
            cancellationToken).ConfigureAwait(false);

        if (evaluation.Outcome == SettingWriteOutcome.Accepted)
            await UpsertAsync(conn, tx, definition, scopeId, actor.Id, evaluation.ValueJson, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SettingDefinition definition, string scopeId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _lockSql;
        cmd.Parameters.AddWithValue("lock_key", $"setting:{ScopeName(definition.Scope)}:{scopeId}:{definition.Name}");
        await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadStoredAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SettingDefinition definition, string scopeId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _selectSql;
        AddKey(cmd, definition, scopeId);
        return (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SettingDefinition definition, string scopeId, string actorId, string valueJson, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _upsertSql;
        AddKey(cmd, definition, scopeId);
        cmd.Parameters.AddWithValue("value", valueJson);
        cmd.Parameters.AddWithValue("updated_by", actorId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddKey(NpgsqlCommand cmd, SettingDefinition definition, string scopeId)
    {
        cmd.Parameters.AddWithValue("key", definition.Name);
        cmd.Parameters.AddWithValue("scope", ScopeName(definition.Scope));
        cmd.Parameters.AddWithValue("scope_id", scopeId);
    }

    /// <summary>Install settings take no scope id; project and agent settings must name one. Matches the table's CHECK.</summary>
    private static string? ScopeError(SettingDefinition definition, string scopeId) => definition.Scope switch
    {
        SettingScope.Install when scopeId.Length > 0 => $"'{definition.Name}' is an install setting and takes no scope id.",
        SettingScope.Install => null,
        _ when scopeId.Length == 0 => $"'{definition.Name}' is a {ScopeName(definition.Scope)} setting and needs a scope id.",
        _ => null,
    };

    private static string ScopeName(SettingScope scope) => scope.ToString().ToLowerInvariant();

    private static bool SameJson(string stored, string canonical) =>
        JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(canonical));
}
