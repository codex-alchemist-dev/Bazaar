using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Garnet;

/// <summary>
/// Runs the hidden Garnet child and keeps it up (docs/design/42-deps-as-modules.md section 6.2). Idle unless
/// <see cref="GarnetCacheOptions.Managed"/> is set. Each start picks the port (0 means a free loopback port), builds the launch arguments (see <see cref="GarnetLaunchSpec.Build"/>),
/// and records the child's process id in the pid file. A failed start, a child that never answers PING, or a crash
/// waits with exponential backoff. When <see cref="GarnetSupervisionOptions.MaxFailures"/> failures fall inside
/// <see cref="GarnetSupervisionOptions.FailureWindow"/>, the circuit breaker opens and the child stays stopped until the host restarts.
/// <para>
/// At startup a stale child from an earlier host is swept (its pid file names a live server, which is killed). The child also dies with
/// the host through the Windows Job object (see <see cref="GarnetProcessLauncher"/>). A restarted child recovers from its append-only file
/// (<c>-r</c>); the first child of a host run starts clean.
/// </para>
/// <para>
/// Nothing escapes <see cref="ExecuteAsync"/> except host shutdown. Every failure becomes a state and a wait, so a broken cache never stops
/// the host. The cache is rebuildable (doc 11), so a lost child costs only cache misses.
/// </para>
/// </summary>
public sealed partial class GarnetServerSupervisor : BackgroundService, IValkeyChildStatus
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _pingTimeout = TimeSpan.FromSeconds(1);

    private readonly GarnetCacheOptions _cache;
    private readonly GarnetSupervisionOptions _supervision;
    private readonly IGarnetProcessLauncher _launcher;
    private readonly IValkeyClientFactory _clientFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<GarnetServerSupervisor> _logger;
    private readonly GarnetManagedConnection _connection;
    private readonly GarnetStaleChildSweeper _sweeper;
    private readonly List<DateTimeOffset> _failures = [];
    private volatile ValkeyChildState _state = ValkeyChildState.Disabled;
    private int _starts;
    private int _restarts;

    /// <summary>Creates the supervisor. Nothing starts until the host runs it.</summary>
    public GarnetServerSupervisor(
        GarnetCacheOptions cache,
        GarnetSupervisionOptions supervision,
        IGarnetProcessLauncher launcher,
        IValkeyClientFactory clientFactory,
        TimeProvider clock,
        ILogger<GarnetServerSupervisor> logger,
        GarnetManagedConnection connection,
        GarnetStaleChildSweeper sweeper)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(supervision);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sweeper);
        _cache = cache;
        _supervision = supervision;
        _launcher = launcher;
        _clientFactory = clientFactory;
        _clock = clock;
        _logger = logger;
        _connection = connection;
        _sweeper = sweeper;
    }

    /// <inheritdoc />
    public ValkeyChildState State => _state;

    /// <inheritdoc />
    public int Restarts => _restarts;

    /// <summary>
    /// True when the data directory holds anything besides the pid file, so a new host run on the same directory recovers the append-only
    /// file instead of starting empty. A missing or empty directory is a first start.
    /// </summary>
    private bool HasRecoverableData()
    {
        try
        {
            return Directory.Exists(_cache.DataDirectory)
                && Directory.EnumerateFileSystemEntries(_cache.DataDirectory)
                    .Any(entry => !string.Equals(Path.GetFileName(entry), GarnetPidFile.FileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The pid file path inside the runtime directory.</summary>
    internal string PidFilePath => Path.Combine(_cache.DataDirectory, GarnetPidFile.FileName);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cache.Managed)
        {
            _state = ValkeyChildState.Disabled;
            return;
        }

        try
        {
            try
            {
                PrepareRuntimeDirectory();
                if (_sweeper.Sweep(PidFilePath, Path.GetFileName(_cache.BinaryPath)))
                {
                    LogStaleChildKilled();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Every start writes the pid file, so the attempts below fail visibly and the breaker counts them.
                LogAttemptFailed(ex.GetType().Name);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_starts > 0)
                {
                    _restarts++;
                }

                _starts++;
                _state = ValkeyChildState.Starting;
                var stopRequested = await RunChildOnceAsync(stoppingToken);
                if (stopRequested)
                {
                    break;
                }

                var now = _clock.GetUtcNow();
                _failures.RemoveAll(t => now - t > _supervision.FailureWindow);
                _failures.Add(now);
                if (_failures.Count >= _supervision.MaxFailures)
                {
                    _state = ValkeyChildState.Tripped;
                    LogBreakerOpen(_failures.Count, _supervision.FailureWindow);
                    return;
                }

                var wait = BackoffFor(_failures.Count);
                _state = ValkeyChildState.Backoff;
                LogRestartScheduled(wait);
                await Task.Delay(wait, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: the child is stopped in RunChildOnceAsync's finally block.
        }
        finally
        {
            // A tripped breaker stays visible to the liveness probe; only a host stop moves the state to Stopped.
            if (_state != ValkeyChildState.Tripped)
            {
                _state = ValkeyChildState.Stopped;
            }
        }
    }

    /// <summary>The wait before the next attempt: base times 2^(failures-1), capped at the maximum.</summary>
    internal TimeSpan BackoffFor(int failures)
    {
        var exponent = Math.Min(Math.Max(failures - 1, 0), 30);
        var seconds = Math.Min(_supervision.BackoffBase.TotalSeconds * Math.Pow(2, exponent), _supervision.BackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Asks the operating system for a free TCP port on loopback. There is a small race before the child binds it; a failed start retries.</summary>
    internal static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Starts the child once and waits until it is supervised. Returns true when the host requested the stop, false when the attempt failed
    /// (a start error, no PING in time, or a crash). The child is always killed and disposed, and the pid file removed, before returning.
    /// </summary>
    private async Task<bool> RunChildOnceAsync(CancellationToken stoppingToken)
    {
        IValkeyChild? child = null;
        try
        {
            var port = _cache.ManagedPort == 0 ? FreeLoopbackPort() : _cache.ManagedPort;
            _connection.Prepare(port);
            var spec = GarnetLaunchSpec.Build(_cache, port, _connection.PasswordForChild(), recover: _starts > 1 || HasRecoverableData());
            child = await _launcher.StartAsync(spec, stoppingToken);
            WritePidFile(child.ProcessId);

            if (!await WaitUntilAnsweringAsync(port, stoppingToken))
            {
                LogNoAnswer(_supervision.StartupTimeout);
                return stoppingToken.IsCancellationRequested;
            }

            _connection.MarkReady();
            _state = ValkeyChildState.Running;
            await child.Exited.WaitAsync(stoppingToken);
            return false;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            // The exception type only: a launch failure message can echo the binary path.
            LogAttemptFailed(ex.GetType().Name);
            return false;
        }
        finally
        {
            _connection.MarkUnavailable();
            if (child is not null)
            {
                child.Kill();
                await child.DisposeAsync();
            }

            GarnetPidFile.Delete(PidFilePath);
        }
    }

    /// <summary>Polls PING until it answers or the startup timeout passes. Throws only when the host is stopping.</summary>
    private async Task<bool> WaitUntilAnsweringAsync(int port, CancellationToken stoppingToken)
    {
        var deadline = _clock.GetUtcNow() + _supervision.StartupTimeout;
        await using var client = _clientFactory.Create(_connection.BuildConnectionString(port));
        while (true)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                attempt.CancelAfter(_pingTimeout);
                await client.PingAsync(attempt.Token);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Not up yet. The deadline below decides when to give up.
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(_pollInterval, _clock, stoppingToken);
        }
    }

    /// <summary>Creates the data directory (append-only file, checkpoints, pid file). Nothing secret lives here.</summary>
    private void PrepareRuntimeDirectory()
    {
        // The directory inherits the ACL of its parent. The operator places DataDirectory under a location only the service account can write.
        Directory.CreateDirectory(_cache.DataDirectory);
    }

    private void WritePidFile(int pid) =>
        File.WriteAllText(PidFilePath, pid.ToString(CultureInfo.InvariantCulture));

    [LoggerMessage(Level = LogLevel.Error, Message = "Managed Garnet failed {Failures} times within {Window}. The circuit breaker is open; the cache stays unavailable until the host restarts.")]
    private partial void LogBreakerOpen(int failures, TimeSpan window);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed Garnet stopped. Restarting in {Wait}.")]
    private partial void LogRestartScheduled(TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed Garnet did not answer PING within {Timeout}.")]
    private partial void LogNoAnswer(TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed Garnet attempt failed ({ExceptionType}).")]
    private partial void LogAttemptFailed(string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killed a stale managed Garnet left by an earlier host.")]
    private partial void LogStaleChildKilled();
}
