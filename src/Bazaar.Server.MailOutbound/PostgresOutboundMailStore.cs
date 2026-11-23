using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Mail;
using Npgsql;

namespace Bazaar.Server.MailOutbound;

/// <summary>
/// Durable <see cref="IOutboundMailStore"/> over <c>mail_message</c> and <c>mail_outbound</c> (mail 0021 and 0023). A new send inserts the message row
/// and the receipt row in one transaction, so a key is never half stored.
/// </summary>
/// <remarks>
/// The recipients, subject and text are one JSON envelope encrypted through <see cref="IMailBodyProtector"/> into the message row's body columns; no
/// statement writes them in clear. A send that ends in Failed, Rejected or Expired clears those columns in the same transaction. Raw SQL with
/// positional parameters, like the quarantine store. The From mailbox must already exist (<c>mail_domain</c> and <c>mail_mailbox</c>).
/// </remarks>
public sealed class PostgresOutboundMailStore(NpgsqlDataSource dataSource, IMailBodyProtector protector) : IOutboundMailStore
{
    private const string _mailbox =
        "SELECT m.id FROM mail_mailbox m JOIN mail_domain d ON d.id = m.domain_id WHERE d.domain = $1 AND m.local_part = $2";
    private const string _insertMessage =
        "INSERT INTO mail_message (id, mailbox_id, direction, state, body_key_id, body_ct, body_nonce) VALUES ($1, $2, 'outbound', $3, $4, $5, $6)";
    private const string _insertOutbound =
        "INSERT INTO mail_outbound (idempotency_key, message_id, approval_id, status, attempts, next_attempt_at, updated_at, tier, reason, footer) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT (idempotency_key) DO NOTHING RETURNING idempotency_key";
    private const string _select =
        "SELECT o.idempotency_key, o.message_id, o.approval_id, o.status, o.attempts, o.next_attempt_at, o.updated_at, o.tier, o.reason, o.footer, " +
        "m.backend_id, m.body_key_id, m.body_ct, m.body_nonce " +
        "FROM mail_outbound o JOIN mail_message m ON m.id = o.message_id WHERE o.idempotency_key = $1";
    private const string _updateOutbound =
        "UPDATE mail_outbound SET status = $4, approval_id = $5, attempts = $6, next_attempt_at = $7, updated_at = $8 " +
        "WHERE idempotency_key = $1 AND status = $2 AND attempts = $3 RETURNING message_id";
    private const string _updateMessage =
        "UPDATE mail_message SET state = $2, backend_id = coalesce($3, backend_id) WHERE id = $1";
    private const string _clearBody =
        "UPDATE mail_message SET body_ct = NULL, body_key_id = NULL, body_nonce = NULL WHERE id = $1";
    private const string _due =
        "SELECT idempotency_key FROM mail_outbound WHERE status = 'awaiting_approval' " +
        "OR (status = 'pending' AND next_attempt_at <= $1) OR (status = 'sending' AND updated_at <= $2) ORDER BY updated_at, idempotency_key LIMIT $3";

    public async Task<StoredOutbound?> FindAsync(string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await using var cmd = dataSource.CreateCommand(_select);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = idempotencyKey });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        OutboundRequest? request = null;
        if (!reader.IsDBNull(12))
        {
            var body = new ProtectedBody(reader.GetGuid(11), reader.GetFieldValue<byte[]>(12), reader.GetFieldValue<byte[]>(13));
            var envelope = JsonSerializer.Deserialize(await protector.UnprotectAsync(body, ct).ConfigureAwait(false), OutboundEnvelopeContext.Default.OutboundEnvelope);
            request = envelope?.ToRequest();
        }

        return new StoredOutbound(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            ParseStatus(reader.GetString(3)),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            reader.IsDBNull(7) ? 0 : reader.GetInt16(7),
            !reader.IsDBNull(8) && Enum.TryParse<OutboundReason>(reader.GetString(8), out var reason) ? reason : default,
            reader.GetFieldValue<DateTimeOffset>(6),
            request,
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    public async Task<(StoredOutbound Record, bool Created)> TryInsertAsync(StoredOutbound record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var request = record.Request ?? throw new ArgumentException("A new send carries its request.", nameof(record));

        var envelope = JsonSerializer.SerializeToUtf8Bytes(OutboundEnvelope.From(request), OutboundEnvelopeContext.Default.OutboundEnvelope);
        var body = await protector.ProtectAsync(request.AgentId, envelope, ct).ConfigureAwait(false);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        Guid mailboxId;
        await using (var lookup = new NpgsqlCommand(_mailbox, connection, tx))
        {
            lookup.Parameters.Add(new NpgsqlParameter<string> { TypedValue = request.From.Domain.ToLowerInvariant() });
            lookup.Parameters.Add(new NpgsqlParameter<string> { TypedValue = request.From.LocalPart.ToLowerInvariant() });
            mailboxId = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid found
                ? found
                : throw new InvalidOperationException("The sending mailbox is not provisioned.");
        }

        await using (var message = new NpgsqlCommand(_insertMessage, connection, tx))
        {
            message.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = record.MessageId });
            message.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = mailboxId });
            message.Parameters.Add(new NpgsqlParameter<string> { TypedValue = MessageState(record.Status) });
            message.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = body.KeyId });
            message.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = body.Ciphertext });
            message.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = body.Nonce });
            await message.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        bool inserted;
        await using (var outbound = new NpgsqlCommand(_insertOutbound, connection, tx))
        {
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.IdempotencyKey });
            outbound.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = record.MessageId });
            outbound.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = record.ApprovalId });
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = StatusText(record.Status) });
            outbound.Parameters.Add(new NpgsqlParameter<int> { TypedValue = record.Attempts });
            outbound.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = record.NextAttemptAt?.ToUniversalTime() });
            outbound.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = record.UpdatedAt.ToUniversalTime() });
            outbound.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)record.Tier });
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Reason.ToString() });
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Footer });
            inserted = await outbound.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }

        if (inserted)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (record, true);
        }

        // The key already exists: drop the message row just inserted and report the stored one.
        await tx.RollbackAsync(ct).ConfigureAwait(false);
        var existing = await FindAsync(record.IdempotencyKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A stored send vanished while it was being created.");
        return (existing, false);
    }

    public async Task<bool> TryUpdateAsync(StoredOutbound expected, StoredOutbound updated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        Guid messageId;
        await using (var outbound = new NpgsqlCommand(_updateOutbound, connection, tx))
        {
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = expected.IdempotencyKey });
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = StatusText(expected.Status) });
            outbound.Parameters.Add(new NpgsqlParameter<int> { TypedValue = expected.Attempts });
            outbound.Parameters.Add(new NpgsqlParameter<string> { TypedValue = StatusText(updated.Status) });
            outbound.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = updated.ApprovalId });
            outbound.Parameters.Add(new NpgsqlParameter<int> { TypedValue = updated.Attempts });
            outbound.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = updated.NextAttemptAt?.ToUniversalTime() });
            outbound.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = updated.UpdatedAt.ToUniversalTime() });
            if (await outbound.ExecuteScalarAsync(ct).ConfigureAwait(false) is not Guid id)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            messageId = id;
        }

        await using (var message = new NpgsqlCommand(_updateMessage, connection, tx))
        {
            message.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
            message.Parameters.Add(new NpgsqlParameter<string> { TypedValue = MessageState(updated.Status) });
            message.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = updated.BackendMessageId });
            await message.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (updated.Status is OutboundStatus.Failed or OutboundStatus.Rejected or OutboundStatus.Expired)
        {
            await using var clear = new NpgsqlCommand(_clearBody, connection, tx);
            clear.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListDueAsync(DateTimeOffset now, DateTimeOffset staleSendingBefore, int max, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_due);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = staleSendingBefore.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(0, max) });
        var keys = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetString(0));
        return keys;
    }

    private static string StatusText(OutboundStatus status) => status switch
    {
        OutboundStatus.AwaitingApproval => "awaiting_approval",
        OutboundStatus.Pending => "pending",
        OutboundStatus.Sending => "sending",
        OutboundStatus.Sent => "sent",
        OutboundStatus.Failed => "failed",
        OutboundStatus.Bounced => "bounced",
        OutboundStatus.Rejected => "rejected",
        OutboundStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static OutboundStatus ParseStatus(string text) => text switch
    {
        "awaiting_approval" => OutboundStatus.AwaitingApproval,
        "pending" => OutboundStatus.Pending,
        "sending" => OutboundStatus.Sending,
        "sent" => OutboundStatus.Sent,
        "failed" => OutboundStatus.Failed,
        "bounced" => OutboundStatus.Bounced,
        "rejected" => OutboundStatus.Rejected,
        "expired" => OutboundStatus.Expired,
        _ => throw new InvalidOperationException("Unknown outbound status in the store."),
    };

    /// <summary>The <c>mail_message.state</c> that goes with an outbound status.</summary>
    private static string MessageState(OutboundStatus status) => status switch
    {
        OutboundStatus.AwaitingApproval or OutboundStatus.Pending => "queued",
        OutboundStatus.Sending => "sending",
        OutboundStatus.Sent => "sent",
        _ => "failed",
    };
}

/// <summary>The request as it is encrypted into the message row. A DTO, so the stored shape does not move with the in-memory record.</summary>
internal sealed record OutboundEnvelope(
    string AgentId,
    string Domain,
    string LocalPart,
    string[] Recipients,
    string Subject,
    string TextBody,
    int AttachmentCount,
    long AttachmentBytes,
    int Taint,
    bool UsesTemplate,
    bool DerivedFromInboundMail,
    bool IsSignUpReply,
    string Nonce)
{
    public static OutboundEnvelope From(OutboundRequest r) => new(
        r.AgentId, r.From.Domain, r.From.LocalPart, [.. r.Recipients], r.Subject, r.TextBody, r.AttachmentCount, r.AttachmentBytes, (int)r.Taint,
        r.UsesTemplate, r.DerivedFromInboundMail, r.IsSignUpReply, r.Nonce);

    public OutboundRequest ToRequest() => new(
        AgentId, new MailboxRef(Domain, LocalPart), Recipients, Subject, TextBody, AttachmentCount, AttachmentBytes, (MailTaint)Taint, UsesTemplate,
        DerivedFromInboundMail, IsSignUpReply, Nonce);
}

[JsonSerializable(typeof(OutboundEnvelope))]
internal sealed partial class OutboundEnvelopeContext : JsonSerializerContext;
