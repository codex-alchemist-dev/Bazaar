using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Bazaar.Server.Valkey;

/// <summary>
/// Runs the hidden <c>valkey-server</c> child and keeps it up (docs/design/42-deps-as-modules.md section 6.2). Idle unless
/// <see cref="ValkeyCacheOptions.Managed"/> is set. A failed start, a child that never answers PING, or a crash waits with exponential
/// backoff. When <see cref="ValkeySupervisionOptions.MaxFailures"/> failures fall inside <see cref="ValkeySupervisionOptions.FailureWindow"/>,
/// the circuit breaker opens and the child stays stopped until the host restarts.
/// <para>
/// Nothing escapes <see cref="ExecuteAsync"/> except host shutdown. Every failure becomes a state and a wait, so a broken cache never stops
/// the host. The cache is rebuildable (doc 11), so a restarted child starts empty and nothing is replayed.
/// </para>
/// </summary>
public sealed partial class ValkeyServerSupervisor : BackgroundService, IValkeyChildStatus
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _pingTimeout = TimeSpan.FromSeconds(1);

    private readonly ValkeyCacheOptions _cache;
    private readonly ValkeySupervisionOptions _supervision;
    private readonly IValkeyProcessLauncher _launcher;
    private readonly IValkeyClientFactory _clientFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<ValkeyServerSupervisor> _logger;
    private readonly List<DateTimeOffset> _failures = [];
    private volatile ValkeyChildState _state = ValkeyChildState.Disabled;
    private int _starts;
    private int _restarts;

    /// <summary>Creates the supervisor. Nothing starts until the host runs it.</summary>
    public ValkeyServerSupervisor(
        ValkeyCacheOptions cache,
        ValkeySupervisionOptions supervision,
        IValkeyProcessLauncher launcher,
        IValkeyClientFactory clientFactory,
        TimeProvider clock,
        ILogger<ValkeyServerSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(supervision);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _cache = cache;
        _supervision = supervision;
        _launcher = launcher;
        _clientFactory = clientFactory;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValkeyChildState State => _state;

    /// <inheritdoc />
    public int Restarts => _restarts;

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Managed Valkey failed {Failures} times within {Window}. The circuit breaker is open; the cache stays unavailable until the host restarts.")]
    private partial void LogBreakerOpen(int failures, TimeSpan window);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed Valkey stopped. Restarting in {Wait}.")]
    private partial void LogRestartScheduled(TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed Valkey did not answer PING within {Timeout}.")]
    private partial void LogNoAnswer(TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed Valkey attempt failed ({ExceptionType}).")]
    private partial void LogAttemptFailed(string exceptionType);

    /// <summary>
    /// Starts the child once and waits until it is supervised. Returns true when the stop was requested by the host, false when the attempt
    /// failed (a start error, no PING in time, or a crash). The child is always killed and disposed before returning.
    /// </summary>
    private async Task<bool> RunChildOnceAsync(CancellationToken stoppingToken)
    {
        IValkeyChild? child = null;
        try
        {
            child = await _launcher.StartAsync(new ValkeyLaunchSpec(_cache.BinaryPath, _cache.ManagedPort), stoppingToken);
            if (!await WaitUntilAnsweringAsync(stoppingToken))
            {
                LogNoAnswer(_supervision.StartupTimeout);
                return stoppingToken.IsCancellationRequested;
            }

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
            if (child is not null)
            {
                child.Kill();
                await child.DisposeAsync();
            }
        }
    }

    /// <summary>Polls PING until it answers or the startup timeout passes. Throws only when the host is stopping.</summary>
    private async Task<bool> WaitUntilAnsweringAsync(CancellationToken stoppingToken)
    {
        var deadline = _clock.GetUtcNow() + _supervision.StartupTimeout;
        await using var client = _clientFactory.Create($"127.0.0.1:{_cache.ManagedPort}");
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
}
