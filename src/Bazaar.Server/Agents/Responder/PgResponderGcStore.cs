using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Body;
using Npgsql;

namespace Bazaar.Server.Agents.Responder;

/// <summary>
/// The Postgres <see cref="IResponderGcStore"/>. Message text is the latest version of each message, opened by the body codec.
/// The queries have the shape of the wire's GC stream (<c>PgGcStreamSource</c>): one row per message with its current version
/// joined in, and mentions read from <c>gc_mention</c> at the message's first seq. The bounded <c>gc_message</c> subquery runs
/// first, so the joins touch only the rows that are returned.
/// </summary>
public sealed class PgResponderGcStore(NpgsqlDataSource dataSource, IGcBodyCodec bodies) : IResponderGcStore
{
    private const string _gcSql = "SELECT archived_at IS NOT NULL FROM gc WHERE gc_id = $1";

    private const string _membersSql = "SELECT principal_id FROM gc_member_current WHERE gc_id = $1 AND left_seq IS NULL";

    private const string _hasEventSql = """
        SELECT EXISTS (SELECT 1 FROM gc_event WHERE gc_id = $1 AND author_id = $2 AND client_msg_id = $3)
        """;

    private const string _newMessagesSql = """
        SELECT m.first_seq, m.message_id, e.author_kind, m.author_id, v.body_ct, v.body_nonce, v.key_id,
               ARRAY(SELECT x.target_principal_id FROM gc_mention x
                     WHERE x.gc_id = m.gc_id AND x.seq = m.first_seq ORDER BY x.target_principal_id) AS mentions
        FROM (SELECT * FROM gc_message WHERE gc_id = $1 AND first_seq > $2 AND first_seq <= $3 ORDER BY first_seq LIMIT $4) m
        JOIN gc_event e ON e.gc_id = m.gc_id AND e.seq = m.first_seq
        CROSS JOIN LATERAL (
            SELECT body_ct, body_nonce, key_id
            FROM gc_message_version
            WHERE gc_id = m.gc_id AND message_id = m.message_id
            ORDER BY version DESC
            LIMIT 1) v
        WHERE v.body_ct IS NOT NULL AND e.source_trust <> 'quarantined'
        ORDER BY m.first_seq
        """;

    private const string _historySql = """
        SELECT m.first_seq, m.message_id, e.author_kind, m.author_id, v.body_ct, v.body_nonce, v.key_id,
               ARRAY(SELECT x.target_principal_id FROM gc_mention x
                     WHERE x.gc_id = m.gc_id AND x.seq = m.first_seq ORDER BY x.target_principal_id) AS mentions
        FROM (SELECT * FROM gc_message WHERE gc_id = $1 AND first_seq <= $2 ORDER BY first_seq DESC LIMIT $3) m
        JOIN gc_event e ON e.gc_id = m.gc_id AND e.seq = m.first_seq
        CROSS JOIN LATERAL (
            SELECT body_ct, body_nonce, key_id
            FROM gc_message_version
            WHERE gc_id = m.gc_id AND message_id = m.message_id
            ORDER BY version DESC
            LIMIT 1) v
        WHERE v.body_ct IS NOT NULL AND e.source_trust <> 'quarantined'
        ORDER BY m.first_seq DESC
        """;

    /// <inheritdoc />
    public async Task<ResponderGcSnapshot?> GetSnapshotAsync(Guid gcId, CancellationToken ct)
    {
        await using var gcCmd = dataSource.CreateCommand(_gcSql);
        gcCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        if (await gcCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not bool archived)
            return null;

        await using var memberCmd = dataSource.CreateCommand(_membersSql);
        memberCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        var members = new HashSet<Guid>();
        await using var reader = await memberCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            members.Add(reader.GetGuid(0));
        return new ResponderGcSnapshot(archived, members);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResponderMessage>> ReadNewMessagesAsync(
        Guid gcId, long afterSeq, long throughSeq, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_newMessagesSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterSeq });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = throughSeq });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        return await ReadMessagesAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResponderMessage>> ReadHistoryAsync(Guid gcId, long throughSeq, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_historySql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = throughSeq });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        var newestFirst = await ReadMessagesAsync(cmd, ct).ConfigureAwait(false);
        return newestFirst.Reverse().ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> HasEventAsync(Guid gcId, Guid authorId, string clientMsgId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_hasEventSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = authorId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = clientMsgId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }

    private async Task<IReadOnlyList<ResponderMessage>> ReadMessagesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var messages = new List<ResponderMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var body = reader.GetFieldValue<byte[]>(4);
            var nonce = reader.IsDBNull(5) ? ReadOnlyMemory<byte>.Empty : reader.GetFieldValue<byte[]>(5);
            var keyId = reader.IsDBNull(6) ? Guid.Empty : reader.GetGuid(6);
            messages.Add(new ResponderMessage(
                reader.GetInt64(0),
                reader.GetGuid(1),
                GcEnumText.ParseAuthorKind(reader.GetString(2)),
                reader.GetGuid(3),
                bodies.Open(body, keyId, nonce),
                reader.GetFieldValue<Guid[]>(7)));
        }

        return messages;
    }
}
