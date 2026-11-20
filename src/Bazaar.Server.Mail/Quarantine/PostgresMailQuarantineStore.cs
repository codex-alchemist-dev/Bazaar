using System.Globalization;
using Npgsql;

namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// Durable <see cref="IMailQuarantineStore"/> over <c>mail_message</c> (mail 0021 plus the quarantine columns of mail 0022). It applies
/// <see cref="QuarantineStateMachine"/> under a row lock, so a refused transition changes nothing and two racing transitions cannot both win.
/// </summary>
/// <remarks>
/// Metadata only: no statement here selects <c>body_ct</c>, <c>body_key_id</c>, <c>body_nonce</c> or <c>from_addr</c>, and the sender is kept as a
/// domain. Admit does not create a row: the inbound pipeline has already stored the message (state <c>received</c>, body encrypted under the
/// person's key), and admit moves it to <c>held</c> and records the screening facts; an unknown id is <c>NotFound</c>. A transition to purged
/// also clears the encrypted body columns in the same transaction (the raw content is what retention purges); the blob behind
/// <c>blob_ref</c> is the retention job's to delete. Rows in other states (received, delivered, outbound states) are not quarantine items.
/// </remarks>
public sealed class PostgresMailQuarantineStore(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null) : IMailQuarantineStore
{
    private const string _quarantined = "state IN ('held', 'released', 'purged', 'reported') AND person_id IS NOT NULL";
    private const string _columns =
        "id, person_id, sender_domain, screen_codes, auth_json ->> 'summary', coalesce(received_at, created_at), state, retention_until";

    private const string _admit =
        "UPDATE mail_message SET state = 'held', person_id = $2, sender_domain = $3, screen_codes = $4, " +
        "auth_json = jsonb_build_object('summary', $5::text), received_at = $6, retention_until = $7 " +
        "WHERE id = $1 AND direction = 'inbound' AND state = 'received' RETURNING id";
    private const string _get = "SELECT " + _columns + " FROM mail_message WHERE id = $1 AND " + _quarantined;
    private const string _lock = "SELECT " + _columns + " FROM mail_message WHERE id = $1 AND " + _quarantined + " FOR UPDATE";
    private const string _list =
        "SELECT " + _columns + " FROM mail_message WHERE " + _quarantined + " AND ($1::text IS NULL OR state = $1) " +
        "ORDER BY coalesce(received_at, created_at), id";
    private const string _countHeld = "SELECT count(*) FROM mail_message WHERE state = 'held' AND person_id IS NOT NULL";
    private const string _release = "UPDATE mail_message SET state = 'released', released_by = $2, released_at = $3 WHERE id = $1";
    private const string _report = "UPDATE mail_message SET state = 'reported', report_approval_id = $2 WHERE id = $1";
    private const string _purge =
        "UPDATE mail_message SET state = 'purged', purged_at = $2, body_ct = NULL, body_key_id = NULL, body_nonce = NULL WHERE id = $1";
    private const string _digestCounts = "SELECT state, count(*) FROM mail_message WHERE " + _quarantined + " GROUP BY state";
    private const string _topDomains =
        "SELECT sender_domain, count(*) AS n FROM mail_message WHERE state = 'held' AND person_id IS NOT NULL AND sender_domain IS NOT NULL " +
        "GROUP BY sender_domain ORDER BY n DESC, sender_domain COLLATE \"C\" LIMIT $1";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<QuarantineTransitionResult> AdmitAsync(QuarantineItem item, QuarantineActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!QuarantineStateMachine.CanAdmit(actor))
        {
            return new QuarantineTransitionResult(
                actor.Kind == QuarantineActorKind.Agent ? QuarantineTransitionOutcome.AgentCannotChange : QuarantineTransitionOutcome.NotOwner, null);
        }

        var held = item with { State = QuarantineState.Held, SenderDomain = DomainOnly(item.SenderDomain) };
        await using (var cmd = dataSource.CreateCommand(_admit))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = held.Id });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = held.PersonId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = held.SenderDomain });
            cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. held.ReasonCodes] });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = held.AuthResult });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = held.ReceivedAt.ToUniversalTime() });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = held.RetentionUntil?.ToUniversalTime() });
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                return new QuarantineTransitionResult(QuarantineTransitionOutcome.Allowed, await GetAsync(held.Id, ct).ConfigureAwait(false));
            }
        }

        // Nothing was updated: the id is unknown, or the row is not a fresh inbound message. Admitting again never resets an item that moved on.
        var existing = await GetAsync(held.Id, ct).ConfigureAwait(false);
        return new QuarantineTransitionResult(
            existing is null ? QuarantineTransitionOutcome.NotFound : QuarantineTransitionOutcome.IllegalTransition, existing);
    }

    /// <inheritdoc />
    public async Task<QuarantineItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_get);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuarantineItem>> ListAsync(QuarantineState? state, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_list);
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = state is null ? null : Name(state.Value) });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<QuarantineItem>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<QuarantineTransitionResult> TransitionAsync(
        Guid id, QuarantineState target, QuarantineActor actor, QuarantineTransitionContext context, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        QuarantineItem item;
        await using (var lockCmd = new NpgsqlCommand(_lock, conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            await using var reader = await lockCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new QuarantineTransitionResult(QuarantineTransitionOutcome.NotFound, null);
            }

            item = Read(reader);
        }

        var result = QuarantineStateMachine.Apply(item, target, actor, context);
        if (!result.IsAllowed)
        {
            return result;
        }

        var now = _time.GetUtcNow();
        await using (var update = target switch
        {
            QuarantineState.Released => Command(conn, tx, _release, id, new NpgsqlParameter<Guid?> { TypedValue = actor.HumanId }, new NpgsqlParameter<DateTimeOffset> { TypedValue = now }),
            QuarantineState.Reported => Command(conn, tx, _report, id, new NpgsqlParameter<Guid?> { TypedValue = context.ReportApprovalId }),
            QuarantineState.Purged => Command(conn, tx, _purge, id, new NpgsqlParameter<DateTimeOffset> { TypedValue = now }),
            _ => throw new InvalidOperationException($"The state machine allowed an unexpected target '{target}'."),
        })
        {
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<int> CountHeldAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_countHeld);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The owner digest from two aggregate queries: counts per state, and the top sender domains of held items (by count, then name).
    /// It never loads an item, so no reason code, auth summary or body is read.
    /// </summary>
    public async Task<QuarantineDigest> GetDigestAsync(
        DateOnly date, int topDomains = QuarantineDigestBuilder.DefaultTopDomains, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topDomains);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var cmd = dataSource.CreateCommand(_digestCounts))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                counts[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }

        var top = new List<TopSenderDomain>();
        await using (var cmd = dataSource.CreateCommand(_topDomains))
        {
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = topDomains });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                top.Add(new TopSenderDomain(reader.GetString(0), (int)reader.GetInt64(1)));
            }
        }

        return new QuarantineDigest(
            date,
            counts.GetValueOrDefault("held"),
            counts.GetValueOrDefault("released"),
            counts.GetValueOrDefault("purged"),
            counts.GetValueOrDefault("reported"),
            top);
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid id, params NpgsqlParameter[] extra)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        foreach (var p in extra)
        {
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static QuarantineItem Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        r.GetFieldValue<string[]>(3),
        r.IsDBNull(4) ? string.Empty : r.GetString(4),
        r.GetFieldValue<DateTimeOffset>(5),
        Parse(r.GetString(6)),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7));

    private static string Name(QuarantineState state) => state switch
    {
        QuarantineState.Held => "held",
        QuarantineState.Released => "released",
        QuarantineState.Purged => "purged",
        QuarantineState.Reported => "reported",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static QuarantineState Parse(string state) => state switch
    {
        "held" => QuarantineState.Held,
        "released" => QuarantineState.Released,
        "purged" => QuarantineState.Purged,
        "reported" => QuarantineState.Reported,
        _ => throw new InvalidOperationException($"mail_message state '{state}' is not a quarantine state."),
    };

    // Only a domain is stored (the column is checked lower-case with no '@'), so a full address from a caller is cut to its domain.
    private static string DomainOnly(string value)
    {
        var at = value.LastIndexOf('@');
        return (at >= 0 ? value[(at + 1)..] : value).Trim().ToLowerInvariant();
    }
}
