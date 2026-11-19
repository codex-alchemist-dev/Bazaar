using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>The default <see cref="IGcExistence"/>: a parameterised lookup in the <c>gc</c> table.</summary>
public sealed class SqlGcExistence(NpgsqlDataSource dataSource) : IGcExistence
{
    private const string _existsSql = "SELECT EXISTS (SELECT 1 FROM gc WHERE gc_id = @gc_id)";

    public async Task<bool> ExistsAsync(Guid gcId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_existsSql);
        cmd.Parameters.AddWithValue("gc_id", gcId);
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
}
