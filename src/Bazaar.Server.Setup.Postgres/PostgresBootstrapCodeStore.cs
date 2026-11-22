using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// Durable one-time bootstrap code over <c>setup_bootstrap_code</c> (setup migration 0002). It has the shape of the provisioning
/// API's code store; the host adapts it to that interface, so this project does not reference the HTTP module. Only the digest
/// of the code is stored. A code is used at most once, and replacing it starts a new one.
/// </summary>
public sealed class PostgresBootstrapCodeStore(NpgsqlDataSource dataSource)
{
    private const string _upsert =
        "INSERT INTO setup_bootstrap_code (id, digest) VALUES (1, $1) " +
        "ON CONFLICT (id) DO UPDATE SET digest = EXCLUDED.digest, created_at = now(), consumed_at = NULL";

    private const string _active = "SELECT digest FROM setup_bootstrap_code WHERE id = 1 AND consumed_at IS NULL";

    private const string _consume =
        "UPDATE setup_bootstrap_code SET consumed_at = now() WHERE id = 1 AND consumed_at IS NULL AND digest = $1";

    private const string _delete = "DELETE FROM setup_bootstrap_code WHERE id = 1";

    private const int _codeBytes = 16;

    /// <summary>Creates a code, replacing any earlier one, and returns its text once (32 lowercase hex characters).</summary>
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(_codeBytes));
        await using var cmd = dataSource.CreateCommand(_upsert);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = Digest(code) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// Retires the code when <paramref name="presentedCode"/> matches. The digests are compared in constant time, then the row is
    /// marked used in one statement, so two racing calls cannot both succeed. No code, or a wrong one, changes nothing.
    /// </summary>
    public async Task<bool> ConsumeAsync(string presentedCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentedCode);
        var presented = Digest(presentedCode);

        byte[]? stored;
        await using (var read = dataSource.CreateCommand(_active))
        {
            stored = (byte[]?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (stored is null || !CryptographicOperations.FixedTimeEquals(stored, presented))
        {
            return false;
        }

        await using var cmd = dataSource.CreateCommand(_consume);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = presented });
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Removes the code, if any. Used when the owner exists, so no code outlives it.</summary>
    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_delete);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Digest(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}
