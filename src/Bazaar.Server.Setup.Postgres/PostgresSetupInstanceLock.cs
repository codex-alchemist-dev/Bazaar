using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// <see cref="ISetupInstanceLock"/> over a Postgres session advisory lock (doc 45 section 1.2; doc 03 names the lock). Acquiring takes
/// the lock without waiting on a dedicated connection and holds that connection until the lease is disposed.
/// </summary>
/// <remarks>
/// The key is its own, not the host's running-instance key (<c>BAZINST1</c>): the running host holds that one for its whole life,
/// so an apply under it would always fail, from the CLI and from the provisioning API inside the host alike. This key only serialises
/// setup applies against each other. The lock is per database.
/// </remarks>
public sealed class PostgresSetupInstanceLock(NpgsqlDataSource dataSource) : ISetupInstanceLock
{
    /// <summary>Advisory-lock key for setup applies ("BAZSETUP"). Distinct from the host instance key, the migration lock and the audit append key.</summary>
    public const long Key = 0x42415A5345545550;

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = Key });
            if ((bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return new Lease(connection);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        throw new SetupInstanceLockHeldException();
    }

    private sealed class Lease(NpgsqlConnection connection) : IAsyncDisposable
    {
        private NpgsqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            var held = Interlocked.Exchange(ref _connection, null);
            if (held is null)
            {
                return;
            }

            // The connection returns to the pool still open, so release the session lock explicitly.
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", held);
                cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = Key });
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // The session is gone, which has already released the lock.
            }

            await held.DisposeAsync().ConfigureAwait(false);
        }
    }
}
