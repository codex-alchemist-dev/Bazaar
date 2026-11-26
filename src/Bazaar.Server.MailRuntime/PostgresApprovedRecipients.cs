using Bazaar.Server.MailOutbound;
using Npgsql;

namespace Bazaar.Server.MailRuntime;

/// <summary>Whether an approved-recipient entry names one address or a whole domain.</summary>
public enum ApprovedRecipientKind
{
    Address,
    Domain,
}

/// <summary>One row of <c>mail_approved_recipient</c>.</summary>
/// <param name="Kind">An exact address or a domain.</param>
/// <param name="Value">The address or domain, lower case.</param>
/// <param name="IncludeSubdomains">For a domain entry: true when hosts under the domain match too. Never set for an address.</param>
/// <param name="AddedBy">Who added it (an actor id or a decision path name).</param>
/// <param name="ApprovalId">The approval that granted it, when there is one.</param>
public sealed record ApprovedRecipientEntry(ApprovedRecipientKind Kind, string Value, bool IncludeSubdomains, string AddedBy, Guid? ApprovalId);

/// <summary>
/// The operator's approved-recipient list (doc 22, "Approved recipients source"). The only intended writer is the approval queue decision
/// path, which is not built; nothing here is an endpoint.
/// </summary>
public interface IApprovedRecipientStore
{
    /// <summary>Adds an entry. Returns false when it already exists. Throws <see cref="ArgumentException"/> for a malformed value.</summary>
    Task<bool> AddAsync(ApprovedRecipientEntry entry, CancellationToken ct);

    /// <summary>Removes an entry. Returns false when there was none.</summary>
    Task<bool> RemoveAsync(ApprovedRecipientKind kind, string value, CancellationToken ct);

    Task<IReadOnlyList<ApprovedRecipientEntry>> ListAsync(CancellationToken ct);

    /// <summary>True when <paramref name="address"/> is covered by an entry. A malformed address is never covered.</summary>
    Task<bool> IsApprovedAsync(string address, CancellationToken ct);
}

/// <summary>Postgres store over <c>mail_approved_recipient</c> (migration 0025). Raw Npgsql on the shared data source; matching is done in SQL, per call, so a removal applies at once.</summary>
public sealed class PostgresApprovedRecipientStore(NpgsqlDataSource dataSource) : IApprovedRecipientStore
{
    // $1 = whole address, $2 = domain. A subdomain entry matches when the domain ends in "." + the entry; no LIKE, so entries need no escaping.
    private const string _matchSql =
        "SELECT 1 FROM mail_approved_recipient WHERE " +
        "(kind = 'address' AND value = $1) OR " +
        "(kind = 'domain' AND (value = $2 OR (include_subdomains AND right($2, length(value) + 1) = '.' || value))) LIMIT 1";

    public async Task<bool> AddAsync(ApprovedRecipientEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.AddedBy);
        var value = Normalize(entry.Kind, entry.Value);
        if (entry.Kind == ApprovedRecipientKind.Address && entry.IncludeSubdomains)
        {
            throw new ArgumentException("An address entry cannot include subdomains.", nameof(entry));
        }

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO mail_approved_recipient (id, kind, value, include_subdomains, added_by, approval_id) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (kind, value) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = KindText(entry.Kind) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = entry.IncludeSubdomains });
        cmd.Parameters.Add(new NpgsqlParameter { Value = entry.AddedBy });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)entry.ApprovalId ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> RemoveAsync(ApprovedRecipientKind kind, string value, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM mail_approved_recipient WHERE kind = $1 AND value = $2");
        cmd.Parameters.Add(new NpgsqlParameter { Value = KindText(kind) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (value ?? string.Empty).Trim().ToLowerInvariant() });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<ApprovedRecipientEntry>> ListAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT kind, value, include_subdomains, added_by, approval_id FROM mail_approved_recipient ORDER BY kind, value");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ApprovedRecipientEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new ApprovedRecipientEntry(
                reader.GetString(0) == "address" ? ApprovedRecipientKind.Address : ApprovedRecipientKind.Domain,
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }

        return rows;
    }

    public async Task<bool> IsApprovedAsync(string address, CancellationToken ct)
    {
        if (!TrySplit(address, out var whole, out var domain))
        {
            return false;
        }

        await using var cmd = dataSource.CreateCommand(_matchSql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = whole });
        cmd.Parameters.Add(new NpgsqlParameter { Value = domain });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static string KindText(ApprovedRecipientKind kind) => kind == ApprovedRecipientKind.Address ? "address" : "domain";

    private static string Normalize(ApprovedRecipientKind kind, string value)
    {
        if (kind == ApprovedRecipientKind.Address)
        {
            return TrySplit(value, out var whole, out _) ? whole : throw new ArgumentException("Not a valid address.", nameof(value));
        }

        var domain = (value ?? string.Empty).Trim().ToLowerInvariant();
        return IsDomain(domain) ? domain : throw new ArgumentException("Not a valid domain.", nameof(value));
    }

    private static bool TrySplit(string? address, out string whole, out string domain)
    {
        whole = domain = string.Empty;
        var text = address?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1 || text.IndexOf('@') != at || !IsDomain(text[(at + 1)..]))
        {
            return false;
        }

        whole = text;
        domain = text[(at + 1)..];
        return true;
    }

    private static bool IsDomain(string text) =>
        text.Length > 0
        && text[0] != '.' && text[0] != '-' && text[^1] != '.' && text[^1] != '-'
        && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');
}

/// <summary>
/// <see cref="IApprovedRecipients"/> over the operator's list. The list is install wide, so the agent id is not used: an entry approves the
/// recipient for every agent. Without an entry a send stays Tier 2.
/// </summary>
public sealed class StoreApprovedRecipients(IApprovedRecipientStore store) : IApprovedRecipients
{
    public async ValueTask<bool> IsApprovedAsync(string agentId, string address, CancellationToken ct) =>
        await store.IsApprovedAsync(address, ct).ConfigureAwait(false);
}
