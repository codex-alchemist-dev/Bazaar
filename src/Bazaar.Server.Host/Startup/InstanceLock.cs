using Npgsql;

namespace Bazaar.Server.Host.Startup;

/// <summary>The one-instance guard (doc 03 startup step 3): a session advisory lock on a dedicated connection, held until the host stops.</summary>
public interface IInstanceLock : IAsyncDisposable
{
    /// <summary>True while this process holds the lock and its connection is open.</summary>
    bool IsHeld { get; }

    /// <summary>Takes the lock without waiting. Returns false when another instance holds it.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct);

    /// <summary>
    /// Proves the lock is still held, not just that a connection object exists. A lost lock (Postgres restart, killed backend) is re-acquired
    /// when nobody else took it; the default checks <see cref="IsHeld"/> only.
    /// </summary>
    Task<InstanceLockStatus> VerifyAsync(CancellationToken ct) =>
        Task.FromResult(IsHeld ? InstanceLockStatus.Held : InstanceLockStatus.DatabaseUnavailable);
}

/// <summary>Result of <see cref="IInstanceLock.VerifyAsync"/>.</summary>
public enum InstanceLockStatus
{
    /// <summary>This process holds the lock.</summary>
    Held,

    /// <summary>The lock was lost and another instance holds it now: this process must stop.</summary>
    TakenByAnother,

    /// <summary>The lock is not held and the database cannot be reached to take it again; keep trying.</summary>
    DatabaseUnavailable,
}

public sealed class PostgresInstanceLock(DatabaseConnection database) : IInstanceLock
{
    /// <summary>Advisory-lock key for the running host. Distinct from the migration run lock.</summary>
    internal const long Key = 0x42415A494E535431; // "BAZINST1"

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlConnection? _connection;

    public bool IsHeld => _connection is { State: System.Data.ConnectionState.Open };

    public async Task<InstanceLockStatus> VerifyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { } held)
            {
                try
                {
                    // Npgsql notices a dead socket only on use, so State alone says nothing after a restart or pg_terminate_backend.
                    await using var ping = new NpgsqlCommand("SELECT 1", held);
                    await ping.ExecuteScalarAsync(ct);
                    return InstanceLockStatus.Held;
                }
                catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or InvalidOperationException)
                {
                    // The session is gone and took the lock with it.
                    _connection = null;
                    await held.DisposeAsync();
                }
            }

            try
            {
                return await TryAcquireCoreAsync(ct) ? InstanceLockStatus.Held : InstanceLockStatus.TakenByAnother;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException)
            {
                return InstanceLockStatus.DatabaseUnavailable;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await TryAcquireCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryAcquireCoreAsync(CancellationToken ct)
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
        await _gate.WaitAsync();
        try
        {
            await ReleaseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ReleaseAsync()
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

/// <summary>
/// Keeps the instance lock honest after startup (doc 03: a second instance exits). Re-checks the lock on an interval: a lock lost to a
/// Postgres restart is taken again; if another instance took it meanwhile, this one logs once and stops with exit code 1 instead of
/// running beside it.
/// </summary>
public sealed partial class InstanceLockWatchdog(
    IInstanceLock instanceLock,
    HostState state,
    HostExitCode exitCode,
    IHostApplicationLifetime lifetime,
    ILogger<InstanceLockWatchdog> logger,
    TimeSpan? interval = null) : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval ?? DefaultInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!state.Started)
                {
                    continue;
                }

                if (!await CheckOnceAsync(stoppingToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One check. Returns false once the host was told to stop because another instance owns the lock.</summary>
    internal async Task<bool> CheckOnceAsync(CancellationToken ct)
    {
        InstanceLockStatus status;
        try
        {
            status = await instanceLock.VerifyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckFailed(logger, ex);
            return true;
        }

        switch (status)
        {
            case InstanceLockStatus.TakenByAnother:
                LogLockTaken(logger);
                exitCode.Value = 1;
                lifetime.StopApplication();
                return false;
            case InstanceLockStatus.DatabaseUnavailable:
                LogUnavailable(logger);
                return true;
            default:
                return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The instance lock was lost and another Bazaar.Server instance holds it now; stopping this one.")]
    private static partial void LogLockTaken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The instance lock is not held and the database is unreachable; retrying.")]
    private static partial void LogUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instance lock check failed; retrying.")]
    private static partial void LogCheckFailed(ILogger logger, Exception ex);
}
