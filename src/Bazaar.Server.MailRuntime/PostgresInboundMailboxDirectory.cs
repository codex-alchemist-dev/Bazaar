using Bazaar.Server.Mail;
using Bazaar.Server.MailInbound;
using Npgsql;

namespace Bazaar.Server.MailRuntime;

/// <summary>
/// The inbound worker's mailbox list over <c>mail_mailbox</c> and <c>mail_domain</c> (doc 22, "Mailbox directory"). A mailbox is listed only when
/// everything the pipeline needs is known: it is active, its domain is active, it has a <c>person_id</c> (the agent's principal id), the person has
/// exactly one open inbox GC (<c>gc.type_key = 'inbox'</c>, not archived, the person a current member), and its domain has a back end. Anything
/// less is skipped, never guessed: a skipped mailbox is not pulled, so its mail stays unseen at the provider and nothing is lost or dropped.
/// No domain is hardcoded; every one comes from the row.
/// </summary>
public sealed class PostgresInboundMailboxDirectory : IInboundMailboxDirectory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Lazy<IMailBackendCatalog> _backends;

    public PostgresInboundMailboxDirectory(NpgsqlDataSource dataSource, IMailBackendCatalog backends)
        : this(dataSource, () => backends)
    {
    }

    /// <summary>The catalog is resolved on first use, so building the directory (for example to check readiness) does not need the secret store.</summary>
    public PostgresInboundMailboxDirectory(NpgsqlDataSource dataSource, Func<IMailBackendCatalog> backends)
    {
        _dataSource = dataSource;
        _backends = new Lazy<IMailBackendCatalog>(backends);
    }

    // Reads the GC tables directly (a read-only coupling to GC's schema, as the address directory reads the mail tables). Two or more open inbox
    // GCs for one person is ambiguous, so the count is returned and the row is skipped rather than choosing one.
    private const string _sql = """
        SELECT m.id, m.person_id, m.local_part, d.domain, g.n, g.first_gc
        FROM mail_mailbox m
        JOIN mail_domain d ON d.id = m.domain_id
        LEFT JOIN LATERAL (
            SELECT count(*) AS n, (array_agg(c.gc_id ORDER BY c.gc_id))[1] AS first_gc
            FROM gc c
            JOIN gc_member gm ON gm.gc_id = c.gc_id
            WHERE c.type_key = 'inbox' AND c.archived_at IS NULL AND gm.principal_id = m.person_id AND gm.left_seq IS NULL
        ) g ON true
        WHERE m.state = 'active' AND d.state = 'active' AND m.person_id IS NOT NULL
        ORDER BY d.domain, m.local_part
        """;

    /// <summary>How many active mailboxes the last listing left out (no inbox GC, several, or no back end). For logs and health, never for text.</summary>
    public int LastSkipped { get; private set; }

    public async Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct)
    {
        // The outbound loop refreshes the same catalog; a refresh here keeps the inbound side correct when it runs alone.
        await _backends.Value.RefreshAsync(ct).ConfigureAwait(false);

        var rows = new List<(Guid Id, Guid Person, string Local, string Domain, long Count, Guid? Gc)>();
        await using (var cmd = _dataSource.CreateCommand(_sql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
            }
        }

        var bindings = new List<InboundMailboxBinding>(rows.Count);
        var skipped = 0;
        foreach (var row in rows)
        {
            var backend = _backends.Value.Resolve(row.Domain);
            if (row.Count != 1 || row.Gc is not { } gcId || backend is null)
            {
                skipped++;
                continue;
            }

            bindings.Add(new InboundMailboxBinding(row.Id, row.Person, gcId, new MailboxRef(row.Domain, row.Local), backend));
        }

        LastSkipped = skipped;
        return bindings;
    }
}
