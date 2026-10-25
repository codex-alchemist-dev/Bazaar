using Npgsql;

namespace Bazaar.Server.Host.Startup;

/// <summary>The one-instance guard (doc 03 startup step 3): a session advisory lock on a dedicated connection, held until the host stops.</summary>
public interface IInstanceLock : IAsyncDisposable
{
    /// <summary>True while this process holds the lock and its connection is open.</summary>
    bool IsHeld { get; }

    /// <summary>Takes the lock without waiting. Returns false when another instance holds it.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct);
}

public sealed class PostgresInstanceLock(DatabaseConnection database) : IInstanceLock
{
    /// <summary>Advisory-lock key for the running host. Distinct from the migration run lock.</summary>
    internal const long Key = 0x42415A494E535431; // "BAZINST1"

    private NpgsqlConnection? _connection;

    public bool IsHeld => _connection is { State: System.Data.ConnectionState.Open };

    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        if (IsHeld)
        {
            return true;
        }

        var dataSource = await database.GetAsync(ct);
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection) { Parameters = { new() { Value = Key } } };
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
            {
                _connection = connection;
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            // The connection returns to the pool and stays open, so release the session lock explicitly.
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection) { Parameters = { new() { Value = Key } } };
                await cmd.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // The session is gone, which has already released the lock.
            }

            await connection.DisposeAsync();
        }
    }
}
