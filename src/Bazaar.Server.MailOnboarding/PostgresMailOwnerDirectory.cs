using Npgsql;

namespace Bazaar.Server.MailOnboarding;

/// <summary>
/// <see cref="IMailOwnerDirectory"/> over the <c>human</c> table (remote/0001, doc 32): the one active row with role <c>owner</c>. A read-only coupling to
/// that schema, like the mailbox directory's read of the GC tables. No row, or a suspended or removed owner, means no owner.
/// </summary>
public sealed class PostgresMailOwnerDirectory(NpgsqlDataSource dataSource) : IMailOwnerDirectory
{
    private const string _sql = "SELECT id, display_name FROM human WHERE role = 'owner' AND status = 'active' LIMIT 1";

    public async Task<MailOwner?> GetOwnerAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new MailOwner(reader.GetGuid(0), reader.GetString(1)) : null;
    }
}
