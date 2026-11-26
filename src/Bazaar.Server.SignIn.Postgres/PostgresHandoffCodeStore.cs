using System.Security.Cryptography;
using System.Text;
using Bazaar.Roles.Abstractions;
using Npgsql;

namespace Bazaar.Server.SignIn.Postgres;

/// <summary>
/// The shared <see cref="IHandoffCodeStore"/> over <c>handoff_code</c> (migration <c>signin/0001_handoff_code.sql</c>, doc 32
/// sections 4 and 5, doc 51 Session; PROPOSED). The server issues, and either the server or the separate bazaar-tui process redeems.
/// Codes live 60 s and redeem once: redeem is one <c>DELETE ... RETURNING</c>, so two redeemers racing on a code cannot both win, and
/// the row is gone before the audience and expiry checks, so a wrong-audience or late attempt burns the code as well (the same rule as
/// the in-memory store). Only the SHA-256 hash of a code is stored. Expiry is judged against the caller's <c>now</c>, not the database clock.
/// </summary>
public sealed class PostgresHandoffCodeStore(NpgsqlDataSource dataSource) : IHandoffCodeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int DefaultCapacity = 4096;
    public const int MaxCodeLength = 256;

    internal const string InsertSql =
        "INSERT INTO handoff_code (code_hash, principal_id, principal_kind, device_id, audience, created_at, expires_at) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7)";

    /// <summary>Drops expired rows (a row past its expiry cannot redeem anyway), then the oldest beyond the capacity.</summary>
    internal const string TrimSql =
        "DELETE FROM handoff_code WHERE expires_at <= $1 " +
        "OR code_hash IN (SELECT code_hash FROM handoff_code ORDER BY expires_at DESC OFFSET $2)";

    /// <summary>The atomic redeem: the row is removed and returned in one statement.</summary>
    internal const string RedeemSql =
        "DELETE FROM handoff_code WHERE code_hash = $1 RETURNING principal_id, principal_kind, device_id, audience, expires_at";

    private readonly int _capacity = DefaultCapacity;

    public async Task<string> IssueAsync(SignInIdentity identity, string audience, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!SignInAudiences.IsKnown(audience))
        {
            throw new ArgumentException("Unknown audience.", nameof(audience));
        }

        var code = Base64Url(RandomNumberGenerator.GetBytes(32));

        await using (var trim = dataSource.CreateCommand(TrimSql))
        {
            trim.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            trim.Parameters.Add(new NpgsqlParameter<long> { TypedValue = _capacity - 1 });
            await trim.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = dataSource.CreateCommand(InsertSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Hash(code) });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = identity.PrincipalId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = identity.Kind.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = identity.DeviceId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = audience });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now + Lifetime });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return code;
    }

    public async Task<SignInIdentity?> RedeemAsync(string code, string audience, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
        {
            return null;
        }

        await using var cmd = dataSource.CreateCommand(RedeemSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Hash(code) });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var storedAudience = reader.GetString(3);
        var expiry = reader.GetFieldValue<DateTimeOffset>(4);
        if (now >= expiry || !string.Equals(storedAudience, audience, StringComparison.Ordinal))
        {
            return null;
        }

        if (!Enum.TryParse<PrincipalKind>(reader.GetString(1), ignoreCase: false, out var kind))
        {
            return null;
        }

        return new SignInIdentity(reader.GetGuid(0), kind, reader.GetGuid(2));
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
