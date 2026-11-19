using Npgsql;

namespace Bazaar.Server.MailInbound;

/// <summary>
/// Durable <see cref="IMailMessageStore"/> over <c>mail_message</c> (mail 0021 plus 0022). No new migration: every column used exists. The body is stored
/// only encrypted (<c>body_key_id</c>, <c>body_ct</c>, <c>body_nonce</c>); <c>from_addr</c> is never written, the sender is kept as <c>sender_domain</c>.
/// Only <see cref="ReadSealedBodyAsync"/> and the pending list (for the pipeline's own resume, still encrypted) select the body columns; the first refuses an actor before it queries.
/// </summary>
/// <remarks>
/// Dedupe matches <c>(mailbox, rfc_message_id)</c> or <c>(mailbox, backend_id)</c>. The first is enforced by the unique index of 0021 even with two
/// workers racing (<c>ON CONFLICT DO NOTHING</c>, then a re-read); the second is checked before insert and assumes one worker per mailbox, as the pass does.
/// </remarks>
public sealed class PostgresMailMessageStore(NpgsqlDataSource dataSource) : IMailMessageStore
{
    private const string _find =
        "SELECT id, state, gc_message_id FROM mail_message WHERE mailbox_id = $1 AND direction = 'inbound' " +
        "AND ((rfc_message_id IS NOT NULL AND rfc_message_id = $2) OR backend_id = $3) ORDER BY created_at, id LIMIT 1";
    private const string _insert =
        "INSERT INTO mail_message (id, mailbox_id, direction, rfc_message_id, backend_id, sender_domain, auth_json, screen_codes, state, " +
        "body_key_id, body_ct, body_nonce, received_at) " +
        "VALUES ($1, $2, 'inbound', $3, $4, $5, jsonb_build_object('summary', $6::text), $7, 'received', $8, $9, $10, $11) " +
        "ON CONFLICT (mailbox_id, direction, rfc_message_id) WHERE rfc_message_id IS NOT NULL DO NOTHING RETURNING id";
    private const string _pending =
        "SELECT id, sender_domain, auth_json ->> 'summary', screen_codes, received_at, body_key_id, body_ct, body_nonce FROM mail_message " +
        "WHERE mailbox_id = $1 AND direction = 'inbound' AND state = 'received' AND body_ct IS NOT NULL ORDER BY received_at, created_at, id LIMIT $2";
    private const string _countPending =
        "SELECT count(*) FROM mail_message WHERE mailbox_id = $1 AND direction = 'inbound' AND state = 'received' AND body_ct IS NOT NULL";
    private const string _recordGc = "UPDATE mail_message SET gc_message_id = $2 WHERE id = $1 AND direction = 'inbound' AND state = 'received'";
    private const string _deliver = "UPDATE mail_message SET state = 'delivered' WHERE id = $1 AND direction = 'inbound' AND state = 'received'";
    private const string _readBody = "SELECT body_key_id, body_ct, body_nonce FROM mail_message WHERE id = $1 AND direction = 'inbound'";

    /// <inheritdoc />
    public async Task<InboundInsertResult> InsertReceivedAsync(NewInboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await FindAsync(message, ct).ConfigureAwait(false) is { } found)
        {
            return found;
        }

        await using (var cmd = dataSource.CreateCommand(_insert))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.Id });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.MailboxId });
            cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = message.RfcMessageId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.BackendId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.SenderDomain });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.AuthSummary });
            cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. message.ScreenCodes] });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.Body.KeyId });
            cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = message.Body.Ciphertext });
            cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = message.Body.Nonce });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = message.ReceivedAt.ToUniversalTime() });
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                return new InboundInsertResult(InboundInsertOutcome.Inserted, message.Id, InboundStoreState.Received, null);
            }
        }

        // The unique index fired: another worker stored the same Message-ID first.
        return await FindAsync(message, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("mail_message_insert_conflict_without_row");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingInboundMessage>> ListPendingAsync(Guid mailboxId, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand(_pending);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = mailboxId });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<PendingInboundMessage>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PendingInboundMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetFieldValue<string[]>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                new SealedBody(reader.GetGuid(5), (byte[])reader.GetValue(6), (byte[])reader.GetValue(7))));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> CountPendingAsync(Guid mailboxId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_countPending);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = mailboxId });
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task RecordGcMessageAsync(Guid id, Guid gcMessageId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_recordGc);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcMessageId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> MarkDeliveredAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_deliver);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<MailBodyReadResult> ReadSealedBodyAsync(Guid id, MailAccessor accessor, CancellationToken ct)
    {
        if (!MailBodyAccessPolicy.CanReadRawBody(accessor))
        {
            return new MailBodyReadResult(MailBodyReadOutcome.Denied, null);
        }

        await using var cmd = dataSource.CreateCommand(_readBody);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new MailBodyReadResult(MailBodyReadOutcome.NotFound, null);
        }

        // A purged message has its body columns cleared: the row exists but there is nothing to return.
        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            return new MailBodyReadResult(MailBodyReadOutcome.NotFound, null);
        }

        return new MailBodyReadResult(
            MailBodyReadOutcome.Ok, new SealedBody(reader.GetGuid(0), (byte[])reader.GetValue(1), (byte[])reader.GetValue(2)));
    }

    private async Task<InboundInsertResult?> FindAsync(NewInboundMessage message, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_find);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.MailboxId });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = message.RfcMessageId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.BackendId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var state = reader.GetString(1) switch
        {
            "received" => InboundStoreState.Received,
            "delivered" => InboundStoreState.Delivered,
            _ => InboundStoreState.Held,
        };
        return new InboundInsertResult(
            InboundInsertOutcome.Duplicate, reader.GetGuid(0), state, reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }
}
