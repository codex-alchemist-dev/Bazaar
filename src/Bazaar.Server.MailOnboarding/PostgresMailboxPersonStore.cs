using Npgsql;

namespace Bazaar.Server.MailOnboarding;

/// <summary>
/// <see cref="IMailboxPersonStore"/> over <c>mail_mailbox.person_id</c> (migration mail/0024). The set is one conditional UPDATE, so two callers
/// racing for the same mailbox cannot both win with different agents.
/// </summary>
public sealed class PostgresMailboxPersonStore(NpgsqlDataSource dataSource) : IMailboxPersonStore
{
    private const string _setSql = "UPDATE mail_mailbox SET person_id = $1 WHERE id = $2 AND person_id IS NULL";
    private const string _closeSql = "UPDATE mail_mailbox SET state = 'closed', version = version + 1 WHERE id = $1 AND person_id = $2 AND state <> 'closed'";
    private const string _readSql = "SELECT person_id FROM mail_mailbox WHERE id = $1";

    public async Task<MailboxPersonSetResult> SetPersonAsync(Guid mailboxId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Only a NULL person_id is ever written, so an existing owner (same or other) is never touched.
        await using (var cmd = new NpgsqlCommand(_setSql, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = agentId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = mailboxId });
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                return MailboxPersonSetResult.Set;
            }
        }

        // Not updated: missing, or already owned. Report which.
        var owner = await ReadAsync(conn, mailboxId, ct).ConfigureAwait(false);
        return owner switch
        {
            { Exists: false } => MailboxPersonSetResult.NotFound,
            { PersonId: { } p } when p == agentId => MailboxPersonSetResult.AlreadySet,
            _ => MailboxPersonSetResult.OwnedByOther,
        };
    }

    public async Task<MailboxOwner> GetOwnerAsync(Guid mailboxId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadAsync(conn, mailboxId, ct).ConfigureAwait(false);
    }

    public async Task<MailboxCloseResult> CloseAsync(Guid mailboxId, Guid agentId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // person_id is only read in the WHERE, never written: the link stays as history.
        await using (var cmd = new NpgsqlCommand(_closeSql, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = mailboxId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = agentId });
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                return MailboxCloseResult.Closed;
            }
        }

        // Not updated: missing, someone else's, or already closed. Report which.
        await using var read = new NpgsqlCommand("SELECT person_id, state FROM mail_mailbox WHERE id = $1", conn);
        read.Parameters.Add(new NpgsqlParameter { Value = mailboxId });
        await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return MailboxCloseResult.NotFound;
        }

        var person = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
        return person != agentId ? MailboxCloseResult.NotOwner : MailboxCloseResult.AlreadyClosed;
    }

    private static async Task<MailboxOwner> ReadAsync(NpgsqlConnection conn, Guid mailboxId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_readSql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = mailboxId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new MailboxOwner(false, null);
        }

        return new MailboxOwner(true, reader.IsDBNull(0) ? null : reader.GetGuid(0));
    }
}
