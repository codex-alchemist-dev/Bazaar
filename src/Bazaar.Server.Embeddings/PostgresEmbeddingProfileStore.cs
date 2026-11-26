using Npgsql;

namespace Bazaar.Server.Embeddings;

/// <summary><c>embedding_profile</c> over the shared data source (doc 04). The runtime role may insert rows and update only <c>active</c>.</summary>
public sealed class PostgresEmbeddingProfileStore(NpgsqlDataSource dataSource) : IEmbeddingProfileStore
{
    private const string _columns = "profile_id, provider_id, model_id, dimensions, created_at, active";

    public async Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM embedding_profile WHERE active");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<EmbeddingProfile?> FindAsync(string providerId, string modelId, int dimensions, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM embedding_profile WHERE provider_id = $1 AND model_id = $2 AND dimensions = $3 ORDER BY active DESC, created_at LIMIT 1");
        cmd.Parameters.AddWithValue(providerId);
        cmd.Parameters.AddWithValue(modelId);
        cmd.Parameters.AddWithValue(dimensions);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<EmbeddingProfile> CreateAsync(string providerId, string modelId, int dimensions, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        await using var cmd = dataSource.CreateCommand(
            $"INSERT INTO embedding_profile (provider_id, model_id, dimensions) VALUES ($1, $2, $3) RETURNING {_columns}");
        cmd.Parameters.AddWithValue(providerId);
        cmd.Parameters.AddWithValue(modelId);
        cmd.Parameters.AddWithValue(dimensions);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Read(reader);
    }

    public async Task ActivateAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The partial unique index allows one active row, so the old one is cleared first.
        await using (var clear = new NpgsqlCommand("UPDATE embedding_profile SET active = false WHERE active AND profile_id <> $1", conn, tx))
        {
            clear.Parameters.AddWithValue(profileId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var set = new NpgsqlCommand("UPDATE embedding_profile SET active = true WHERE profile_id = $1", conn, tx))
        {
            set.Parameters.AddWithValue(profileId);
            if (await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException($"Embedding profile {profileId} does not exist.");
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EmbeddingProfile Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetBoolean(5));
}
