using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Walkthrough;

/// <summary>Durable <see cref="IWalkthroughPrefsStore"/> over <c>walkthrough_prefs</c> (walkthrough migration 0001). One row per person; a save replaces it.</summary>
public sealed class PostgresWalkthroughPrefsStore(NpgsqlDataSource dataSource) : IWalkthroughPrefsStore
{
    private const string _get = "SELECT dismissed, finished, current, completed FROM walkthrough_prefs WHERE person_id = $1";
    private const string _set =
        "INSERT INTO walkthrough_prefs (person_id, dismissed, finished, current, completed) VALUES ($1, $2, $3, $4, $5) "
        + "ON CONFLICT (person_id) DO UPDATE SET dismissed = EXCLUDED.dismissed, finished = EXCLUDED.finished, "
        + "current = EXCLUDED.current, completed = EXCLUDED.completed, updated_at = now()";

    public async Task<WalkthroughPrefs?> GetAsync(string personId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        await using var cmd = dataSource.CreateCommand(_get);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = personId });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WalkthroughPrefs(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetString(2), reader.GetFieldValue<string[]>(3));
    }

    public async Task SetAsync(string personId, WalkthroughPrefs prefs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        ArgumentNullException.ThrowIfNull(prefs);
        await using var cmd = dataSource.CreateCommand(_set);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = personId });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = prefs.Dismissed });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = prefs.Finished });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = prefs.Current });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. prefs.Completed], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
