using Bazaar.Archive;
using Npgsql;

namespace Bazaar.Server.Gc.Archive;

/// <summary>
/// The GC side of restore (doc 25 "Archive cold storage"). The GC log is append-only and archiving deletes nothing, so a restore
/// never writes: it reports, for each archived record, whether the live event still matches (<c>already present</c>) or differs
/// (<c>conflict</c>). A record that is missing from the live log cannot be re-inserted under its old seq, so
/// <see cref="WriteAsync"/> refuses with <see cref="NotSupportedException"/> and the caller reports it.
/// </summary>
public sealed class PgGcArchiveRestoreTarget(NpgsqlDataSource dataSource, PgGcArchiveBridge text) : IArchiveRestoreTarget
{
    private const string _sql = "SELECT kind, created_at, message_id, version FROM gc_event WHERE gc_id = $1 AND seq = $2";

    /// <inheritdoc />
    public async Task<string?> GetLiveHashAsync(ArchiveUnit unit, string recordId, CancellationToken ct)
    {
        if (!TryParse(unit, recordId, out var gcId, out var seq))
        {
            return null;
        }

        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        string kind;
        DateTimeOffset at;
        Guid? messageId;
        int? version;
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            kind = reader.GetString(0);
            at = reader.GetFieldValue<DateTimeOffset>(1);
            messageId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            version = reader.IsDBNull(3) ? null : reader.GetInt32(3);
        }

        var body = messageId is { } id && version is { } v
            ? await text.GetMessageTextAsync(gcId, id, v, ct).ConfigureAwait(false) ?? string.Empty
            : string.Empty;
        return ArchiveHashing.RecordHash(new ArchiveRecord(recordId, kind, at, body));
    }

    /// <inheritdoc />
    public Task WriteAsync(ArchiveUnit unit, ArchiveRecord record, CancellationToken ct) =>
        throw new NotSupportedException($"Record '{record.Id}' is missing from the live GC log; the log is append-only and cannot take it back.");

    public static bool TryParse(ArchiveUnit unit, string recordId, out Guid gcId, out long seq)
    {
        gcId = default;
        seq = 0;
        var tail = recordId;
        if (unit.Kind == ArchiveUnitKind.Project)
        {
            var slash = recordId.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0 || !Guid.TryParseExact(recordId[..slash], "D", out gcId))
            {
                return false;
            }

            tail = recordId[(slash + 1)..];
        }
        else if (!Guid.TryParseExact(unit.Id, "D", out gcId))
        {
            return false;
        }

        return tail.StartsWith("seq-", StringComparison.Ordinal)
            && long.TryParse(tail.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seq);
    }
}
