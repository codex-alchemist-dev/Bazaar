using System.Text.Json;
using Npgsql;

namespace Bazaar.Server.GcMetadata;

/// <summary>Postgres <see cref="IGcHeaderSource"/>: <c>gc.type_key</c> and <c>gc.metadata</c> of one GC. Non-string metadata values are skipped.</summary>
public sealed class PgGcHeaderSource(NpgsqlDataSource dataSource) : IGcHeaderSource
{
    /// <inheritdoc />
    public async Task<GcHeader?> FindAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT type_key, metadata::text FROM gc WHERE gc_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!reader.IsDBNull(1))
        {
            using var doc = JsonDocument.Parse(reader.GetString(1));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s)
                        metadata[p.Name] = s;
                }
            }
        }

        return new GcHeader(reader.GetString(0), metadata);
    }
}
