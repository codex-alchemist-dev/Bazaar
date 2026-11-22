using Npgsql;

namespace Bazaar.Server.SignIn.Passkeys;

/// <summary><see cref="IPasskeyCredentialStore"/> over the <c>passkey_credential</c> table.</summary>
public sealed class PostgresPasskeyCredentialStore(NpgsqlDataSource dataSource) : IPasskeyCredentialStore
{
    /// <summary>The lookup: an unrevoked credential whose person is active. Parameter: credential id.</summary>
    public const string FindSql = """
        SELECT c.id, c.human_id, c.credential_id, c.public_key, c.sign_count
        FROM passkey_credential c
        JOIN human h ON h.id = c.human_id
        WHERE c.credential_id = $1 AND c.revoked_at IS NULL AND h.status = 'active'
        """;

    /// <summary>The compare-and-set. Parameters: id, expected count, new count.</summary>
    public const string AdvanceSql = """
        UPDATE passkey_credential SET sign_count = $3
        WHERE id = $1 AND sign_count = $2 AND revoked_at IS NULL
        """;

    /// <inheritdoc />
    public async Task<PasskeyCredential?> FindAsync(byte[] credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        await using var command = dataSource.CreateCommand(FindSql);
        command.Parameters.Add(new NpgsqlParameter { Value = credentialId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PasskeyCredential(reader.GetGuid(0), reader.GetGuid(1), (byte[])reader[2], (byte[])reader[3], reader.GetInt64(4));
    }

    /// <summary>The ids of a person's live credentials. Parameter: human id.</summary>
    public const string IdsSql = "SELECT credential_id FROM passkey_credential WHERE human_id = $1 AND revoked_at IS NULL";

    /// <summary>The insert. Parameters: id, human id, credential id, public key, sign count.</summary>
    public const string AddSql = """
        INSERT INTO passkey_credential (id, human_id, credential_id, public_key, sign_count)
        VALUES ($1, $2, $3, $4, $5)
        ON CONFLICT (credential_id) DO NOTHING
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<byte[]>> CredentialIdsAsync(Guid humanId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(IdsSql);
        command.Parameters.Add(new NpgsqlParameter { Value = humanId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<byte[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add((byte[])reader[0]);
        }

        return ids;
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await using var command = dataSource.CreateCommand(AddSql);
        command.Parameters.Add(new NpgsqlParameter { Value = credential.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = credential.HumanId });
        command.Parameters.Add(new NpgsqlParameter { Value = credential.CredentialId });
        command.Parameters.Add(new NpgsqlParameter { Value = credential.PublicKey });
        command.Parameters.Add(new NpgsqlParameter { Value = credential.SignCount });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryAdvanceSignCountAsync(Guid id, long expected, long updated, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(AdvanceSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = expected });
        command.Parameters.Add(new NpgsqlParameter { Value = updated });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
