using Npgsql;

namespace Bazaar.Server.ThinkingGc;

/// <summary>Postgres-backed <see cref="IThinkingGcLookup"/>: reads the GC's type key.</summary>
public sealed class ThinkingGcLookup(NpgsqlDataSource dataSource) : IThinkingGcLookup
{
    /// <inheritdoc />
    public async Task<bool> IsThinkingGcAsync(Guid gcId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT type_key FROM gc WHERE gc_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string type && type == ThinkingGcStore.TypeKey;
    }
}

/// <summary>A fixed answer, for the configured default and for tests.</summary>
public sealed class StaticThinkingPointerPolicy(bool enabled) : IThinkingPointerPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken) => ValueTask.FromResult(enabled);
}
