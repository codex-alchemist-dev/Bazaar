using Npgsql;

namespace Bazaar.Server.MailRuntime;

/// <summary>Is this address one of the install's own mailboxes? Reads <c>mail_mailbox</c> and <c>mail_domain</c>; a closed mailbox no longer counts as ours.</summary>
public sealed class PostgresBazaarAddressDirectory(NpgsqlDataSource dataSource) : Bazaar.Server.MailOutbound.IBazaarAddressDirectory
{
    private const string _sql =
        "SELECT 1 FROM mail_mailbox m JOIN mail_domain d ON d.id = m.domain_id " +
        "WHERE d.domain = $1 AND m.local_part = $2 AND m.state <> 'closed' AND d.state <> 'retired' LIMIT 1";

    public async ValueTask<bool> IsBazaarAddressAsync(string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return false;
        }

        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = address[(at + 1)..].Trim().ToLowerInvariant() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = address[..at].Trim().ToLowerInvariant() });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }
}

/// <summary>The active domains and where their configuration lives. Reads <c>mail_domain</c> only; the configuration itself is in the secret store.</summary>
public sealed class PostgresMailDomainSource(NpgsqlDataSource dataSource) : IMailDomainSource
{
    public async Task<IReadOnlyList<MailDomainConfig>> ListActiveAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT domain, backend_kind, config_ref FROM mail_domain WHERE state = 'active' ORDER BY domain");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<MailDomainConfig>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MailDomainConfig(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }
}
