using Npgsql;

namespace Bazaar.Server.Company;

/// <summary>
/// The Postgres <see cref="ICompanyProfileStore"/> over <c>company_profile</c>. One transaction under one advisory lock. A profile
/// with other values is refused, never edited. Rollback removes the row only while its slug still matches.
/// </summary>
public sealed class PostgresCompanyProfileStore(NpgsqlDataSource dataSource) : ICompanyProfileStore
{
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended('setup:company.profile', 0))";
    private const string _readSql = "SELECT name, slug, locale, timezone FROM company_profile WHERE id = 1";
    private const string _insertSql = "INSERT INTO company_profile (id, name, slug, locale, timezone) VALUES (1, @name, @slug, @locale, @timezone)";
    private const string _deleteSql = "DELETE FROM company_profile WHERE id = 1 AND slug = @slug";

    /// <inheritdoc />
    public async Task<CompanyProfileResult> EnsureAsync(CompanyProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var locking = new NpgsqlCommand(_lockSql, connection, transaction))
        {
            await locking.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var read = new NpgsqlCommand(_readSql, connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var same = reader.GetString(0) == request.Name && reader.GetString(1) == request.Slug
                    && reader.GetString(2) == request.Locale && reader.GetString(3) == request.Timezone;
                return same
                    ? CompanyProfileResult.AlreadyPresent
                    : CompanyProfileResult.Refused("the install already has a company with other values; one company per install, and setup does not edit it.");
            }
        }

        await using (var insert = new NpgsqlCommand(_insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("name", request.Name);
            insert.Parameters.AddWithValue("slug", request.Slug);
            insert.Parameters.AddWithValue("locale", request.Locale);
            insert.Parameters.AddWithValue("timezone", request.Timezone);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CompanyProfileResult.WasCreated;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        await using var command = dataSource.CreateCommand(_deleteSql);
        command.Parameters.AddWithValue("slug", slug);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
