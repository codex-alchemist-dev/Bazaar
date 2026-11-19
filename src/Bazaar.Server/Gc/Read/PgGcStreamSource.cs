using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Wire.Sync;
using Npgsql;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// The Postgres side of the wire's GC reads (doc 02 WP3, doc 04 replay). It maps the log onto <c>(n, ev)</c> like this:
/// <c>n</c> is the seq of the message's first event, and <c>ev</c> is the seq of the event that wrote the current version.
/// An edit therefore takes an <c>ev</c> and no <c>n</c>, as doc 02 requires, and both counters are gap-free and ordered per GC.
/// Only message events reach the stream. Every other event is skipped.
/// </summary>
/// <remarks>
/// Access is not checked here: the caller has passed <see cref="IGcAccessPolicy"/>. The current-version join looks only at the
/// messages a replay page may return (new ones and edited ones), not at every message in the GC.
/// </remarks>
public sealed class PgGcStreamSource(NpgsqlDataSource dataSource, IGcBodyCodec bodies) : IGcStreamSource
{
    // One row per message: its current version, plus the first event's reply and mentions. @SOURCE is the message source.
    private const string _select = """
        SELECT m.message_id, m.first_seq, m.author_id, v.version, v.seq AS ev, v.body_ct, v.body_nonce, v.key_id,
               v.created_at, e.reply_to_seq,
               ARRAY(SELECT x.target_principal_id FROM gc_mention x
                     WHERE x.gc_id = m.gc_id AND x.seq = m.first_seq ORDER BY x.target_principal_id) AS mentions
        FROM @SOURCE
        CROSS JOIN LATERAL (
            SELECT version, seq, body_ct, body_nonce, key_id, created_at
            FROM gc_message_version
            WHERE gc_id = m.gc_id AND message_id = m.message_id
            ORDER BY version DESC
            LIMIT 1) v
        JOIN gc_event e ON e.gc_id = m.gc_id AND e.seq = m.first_seq
        """;

    // Candidates only: messages newer than the client's n, and messages with an event past its ev (an edit is an event, and the
    // current version is the newest, so any version past ev makes the current one newer too). Without this the current-version
    // lookup ran for every message in the GC on each page, which the dispatcher repeats after each commit.
    private static readonly string _readSql = Wrap(
        """
        (SELECT g.* FROM gc_message g WHERE g.gc_id = $1 AND g.first_seq > $2
         UNION
         SELECT g.* FROM gc_message g
         WHERE g.gc_id = $1
           AND g.message_id IN (SELECT e.message_id FROM gc_event e WHERE e.gc_id = $1 AND e.seq > $3 AND e.message_id IS NOT NULL)) m
        """,
        "WHERE (cur.first_seq > $2 OR cur.ev > $3) AND cur.ev > $4 ORDER BY cur.ev LIMIT $5");

    private static readonly string _tailSql = Wrap(
        "(SELECT * FROM gc_message WHERE gc_id = $1 ORDER BY first_seq DESC LIMIT $2) m",
        "ORDER BY cur.first_seq");

    private static readonly string _historySql = Wrap(
        "(SELECT * FROM gc_message WHERE gc_id = $1 AND first_seq < $2 ORDER BY first_seq DESC LIMIT $3) m",
        "ORDER BY cur.first_seq");

    // Fills the message source into _select and applies the page's own WHERE, ORDER and LIMIT to the current-version rows.
    private static string Wrap(string source, string tail) =>
        "SELECT * FROM (" + _select.Replace("@SOURCE", source, StringComparison.Ordinal) + ") cur " + tail;

    // The newest message seq and the newest message event seq (every version is an event, so ev = its seq).
    private const string _headSql = """
        SELECT (SELECT m.first_seq FROM gc_message m WHERE m.gc_id = g.gc_id ORDER BY m.first_seq DESC LIMIT 1),
               (SELECT e.seq FROM gc_event e WHERE e.gc_id = g.gc_id AND e.message_id IS NOT NULL ORDER BY e.seq DESC LIMIT 1)
        FROM gc g
        WHERE g.gc_id = $1
        """;

    private const string _unreadSql = """
        SELECT COUNT(*)
        FROM gc_message m
        WHERE m.gc_id = $1
          AND m.first_seq > COALESCE((SELECT r.last_read_seq FROM read_state r WHERE r.principal_id = $2 AND r.gc_id = $1), 0)
        """;

    /// <inheritdoc />
    public async ValueTask<GcHead?> GetHeadAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_headSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new GcHead(reader.IsDBNull(0) ? 0 : reader.GetInt64(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WireGcMessage>> ReadAsync(
        Guid gcId, long after, long editAfter, long afterEv, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_readSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = after });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = editAfter });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterEv });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        return await ReadMessagesAsync(cmd, gcId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WireGcMessage>> ReadTailAsync(Guid gcId, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_tailSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        return await ReadMessagesAsync(cmd, gcId, ct).ConfigureAwait(false);
    }

    /// <summary>Up to <paramref name="limit"/> messages with <c>n</c> below <paramref name="before"/>, oldest first, and whether older ones remain.</summary>
    public async ValueTask<GcPage> ReadHistoryAsync(Guid gcId, long before, int limit, CancellationToken ct)
    {
        // One extra row tells whether an older page exists without a second query.
        await using var cmd = dataSource.CreateCommand(_historySql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = before });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit + 1 });
        var rows = await ReadMessagesAsync(cmd, gcId, ct).ConfigureAwait(false);
        var hasMore = rows.Count > limit;
        return new GcPage(hasMore ? rows.Skip(rows.Count - limit).ToList() : rows, hasMore);
    }

    /// <inheritdoc />
    public async ValueTask<int> CountUnreadAsync(Guid gcId, Guid viewerId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_unreadSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = viewerId });
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    private async Task<List<WireGcMessage>> ReadMessagesAsync(NpgsqlCommand cmd, Guid gcId, CancellationToken ct)
    {
        // Read every row first and open the bodies after the reader is closed. Opening a body fetches its key through the same
        // pool, so doing it mid-read held one connection while asking for a second: with as many concurrent replays as the pool
        // has connections (100) every replay waited for a connection the others held, and all of them stalled until the 15 s
        // pool timeout (chaos run: 100 subscribers to a 300-message GC got nothing for 15 s and six never got their replay).
        var rows = new List<(WireGcMessage Message, byte[]? Ciphertext, byte[]? Nonce, Guid KeyId)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    new WireGcMessage(
                        gcId,
                        N: reader.GetInt64(1),
                        Ev: reader.GetInt64(4),
                        MessageId: reader.GetGuid(0),
                        AuthorId: reader.GetGuid(2),
                        Text: string.Empty,
                        Ts: reader.GetFieldValue<DateTimeOffset>(8),
                        Edited: reader.GetInt32(3) > 1,
                        Mentions: reader.GetFieldValue<Guid[]>(10),
                        ReplyToN: reader.IsDBNull(9) ? null : reader.GetInt64(9)),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
                    reader.IsDBNull(7) ? Guid.Empty : reader.GetGuid(7)));
            }
        }

        var items = new List<WireGcMessage>(rows.Count);
        foreach (var (message, ciphertext, nonce, keyId) in rows)
        {
            // A tombstone has no body (docs/design/04): its text is empty.
            if (ciphertext is null)
            {
                items.Add(message);
                continue;
            }

            var text = await bodies.OpenAsync(ciphertext, keyId, nonce ?? [], ct).ConfigureAwait(false) ?? GcBodyText.ErasedText;
            items.Add(message with { Text = text });
        }

        return items;
    }
}
