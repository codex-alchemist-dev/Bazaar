using Npgsql;

namespace Bazaar.Server.Company;

/// <summary>Reads <c>company_profile</c> (migration 0001, company namespace).</summary>
public sealed class PostgresCompanyProfileReader(NpgsqlDataSource dataSource) : ICompanyProfileReader
{
    private const string _sql = "SELECT name, slug, locale, timezone, created_at FROM company_profile WHERE id = 1";

    /// <inheritdoc />
    public async Task<CompanyProfile?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(_sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CompanyProfile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }
}
