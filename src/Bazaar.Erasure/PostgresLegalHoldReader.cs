using Bazaar.Erasure.Abstractions;
using Npgsql;

namespace Bazaar.Erasure;

/// <summary>
/// Reads <c>active_legal_hold</c> (migration erasure/0001) for person-scoped holds. Read-only; holds are created and released
/// only through the owner's signed path, which is not on main yet.
/// </summary>
/// <remarks>
/// Scope kinds <c>gc</c>, <c>company</c> and <c>category</c> are NOT checked here. OPEN for the owner: how a company or
/// category hold covers a person. Until that is decided, a non-person hold does not block erasure of a person.
/// </remarks>
public sealed class PostgresLegalHoldReader(NpgsqlDataSource dataSource) : ILegalHoldReader
{
    private const string _sql = "SELECT EXISTS (SELECT 1 FROM active_legal_hold WHERE scope_kind = 'person' AND scope_value = $1)";

    public async Task<bool> IsPersonHeldAsync(Guid personId, CancellationToken ct = default)
    {
        if (personId == Guid.Empty)
            throw new ArgumentException("A person id is required.", nameof(personId));

        await using var cmd = dataSource.CreateCommand(_sql);
        // uuid::text is lowercase and hyphenated; the scope value is written the same way.
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = personId.ToString("D") });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
}
