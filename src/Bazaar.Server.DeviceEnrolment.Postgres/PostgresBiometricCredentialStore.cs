using Bazaar.Server.Biometric;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres;

/// <summary>
/// <see cref="IBiometricCredentialStore"/> over <c>biometric_credential</c>. The counter advance is one conditional UPDATE, so two
/// racing uses of one assertion cannot both pass.
/// </summary>
public sealed class PostgresBiometricCredentialStore(NpgsqlDataSource dataSource) : IBiometricCredentialStore
{
    public async Task<BiometricCredential?> GetAsync(byte[] credentialId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT human_id, device_id, public_key, sign_count, attested_factors, revoked_at FROM biometric_credential WHERE credential_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = credentialId });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var factors = new HashSet<BiometricFactor>();
        foreach (var name in r.GetFieldValue<string[]>(4))
        {
            if (BiometricNames.TryParseFactor(name, out var f))
                factors.Add(f);
        }

        return new BiometricCredential(
            credentialId,
            r.GetGuid(0),
            r.GetGuid(1).ToString("D"),
            r.GetFieldValue<byte[]>(2),
            checked((uint)r.GetInt64(3)),
            factors,
            Revoked: !r.IsDBNull(5));
    }

    public async Task<bool> AdvanceSignCountAsync(byte[] credentialId, uint expectedCount, uint newCount, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE biometric_credential SET sign_count = $3 WHERE credential_id = $1 AND sign_count = $2 AND revoked_at IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = credentialId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = expectedCount });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = newCount });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task RevokeAsync(byte[] credentialId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE biometric_credential SET revoked_at = now() WHERE credential_id = $1 AND revoked_at IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = credentialId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
