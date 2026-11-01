using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Runs one managed PostgreSQL cluster as a child process: initdb, start with adopt-or-refuse, stop, and restart
/// with bounded backoff after unexpected exits. Reports through <see cref="ProcessEvent"/>; handlers must not throw.
/// </summary>
public sealed class PostgresProcessController : IAsyncDisposable
{
    private const int PasswordByteCount = 32;
    private const string RedactedText = "[redacted]";
    private static readonly TimeSpan ProbeAttemptTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly PostgresProcessOptions _options;
    private readonly IPostgresCommandRunner _runner;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile PostgresProcessState _state = PostgresProcessState.Stopped;
    private volatile bool _stopRequested;
    private volatile bool _adopted;
    private IPostgresChild? _child;
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisor;

    /// <summary>Creates a controller. Throws when the options cannot work.</summary>
    public PostgresProcessController(PostgresProcessOptions options, IPostgresCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        options.Validate();

        _options = options;
        _runner = runner;
    }

    /// <summary>Lifecycle state as the controller sees it.</summary>
    public PostgresProcessState State => _state;

    /// <summary>True when the running cluster was started by an earlier host and adopted, not started by this controller.</summary>
    public bool IsAdopted => _adopted;

    /// <summary>Lifecycle reports (start, exit, restart, shutdown). Handlers must not throw.</summary>
    public event Action<PostgresProcessEvent>? ProcessEvent;

    /// <summary>
    /// Creates the cluster with initdb (scram-sha-256, UTF-8). A random superuser password is generated, handed to
    /// <paramref name="persistSuperuserPassword"/> before initdb runs, and written only to a temp password file that is
    /// deleted afterwards. It never appears in arguments, events or exceptions.
    /// </summary>
    public async Task InitDbAsync(Func<string, CancellationToken, ValueTask> persistSuperuserPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistSuperuserPassword);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(ClusterMarkerPath))
            {
                throw new PostgresProcessException($"The data directory {_options.DataDirectory} already holds a cluster; initdb runs once.");
            }

            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(PasswordByteCount));
            await persistSuperuserPassword(password, cancellationToken);
            await RunInitDbAsync(password, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Starts the cluster and waits until the port accepts connections. If a postmaster.pid names a live process on the
    /// configured port, that cluster is adopted. If the port is held by anything else, the call is refused.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_supervisor is { IsCompleted: false })
            {
                if (_state == PostgresProcessState.Running)
                {
                    return;
                }

                throw new PostgresProcessException("A restart of the managed PostgreSQL process is in progress; stop it before starting.");
            }

            if (!File.Exists(ClusterMarkerPath))
            {
                throw new PostgresProcessException($"No cluster in {_options.DataDirectory}; run InitDbAsync first.");
            }

            var (child, adopted) = await StartCoreAsync(cancellationToken);
            _child = child;
            _adopted = adopted;
            _stopRequested = false;
            _state = PostgresProcessState.Running;

            _supervisorCts?.Dispose();
            _supervisorCts = new CancellationTokenSource();
            _supervisor = SuperviseAsync(child, _supervisorCts.Token);

            Raise(
                adopted ? PostgresProcessEventKind.Adopted : PostgresProcessEventKind.Started,
                adopted
                    ? $"Adopted the running cluster (pid {child.ProcessId}) on port {_options.Port}."
                    : $"Started postgres (pid {child.ProcessId}) on port {_options.Port}.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Stops the cluster: pg_ctl fast shutdown, wait up to ShutdownTimeout, then an immediate shutdown, wait again, then kill.
    /// A stop requested by the caller never triggers a restart.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            _stopRequested = true;
            _supervisorCts?.Cancel();
            if (_supervisor is { } supervisor)
            {
                try
                {
                    await supervisor;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A faulted supervisor must not stop child cleanup below.
                    Raise(PostgresProcessEventKind.ShutdownRequestFailed, $"Supervisor faulted during stop: {ex.Message}");
                }
            }

            var child = _child;
            _child = null;
            _supervisor = null;

            if (child is null)
            {
                _state = PostgresProcessState.Stopped;
                return;
            }

            if (!child.HasExited)
            {
                await ShutDownAsync(child, cancellationToken);
            }

            child.Dispose();
            _adopted = false;
            _state = PostgresProcessState.Stopped;
            Raise(PostgresProcessEventKind.Stopped, $"Stopped postgres (pid {child.ProcessId}).");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _supervisorCts?.Dispose();
        _lifecycleGate.Dispose();
    }

    private string ClusterMarkerPath => Path.Combine(_options.DataDirectory, "PG_VERSION");

    private string ProbeHost => _options.ListenAddress switch
    {
        "0.0.0.0" => "127.0.0.1",
        "::" => "::1",
        var address => address,
    };

    private async Task RunInitDbAsync(string password, CancellationToken cancellationToken)
    {
        var passwordFile = Path.Combine(Path.GetTempPath(), $"bazaar-pg-{Guid.NewGuid():N}.pwfile");
        try
        {
            WritePasswordFile(passwordFile, password);

            IReadOnlyList<string> arguments =
            [
                "-D", _options.DataDirectory,
                "-U", _options.SuperuserName,
                "-A", "scram-sha-256",
                "-E", "UTF8",
                "--pwfile", passwordFile,
            ];

            var result = await _runner.RunAsync(ToolPath("initdb"), arguments, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new PostgresProcessException(
                    $"initdb failed with exit code {result.ExitCode}: {result.Output.Replace(password, RedactedText, StringComparison.Ordinal)}");
            }
        }
        finally
        {
            File.Delete(passwordFile);
        }
    }

    private static void WritePasswordFile(string path, string password)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(password);
        writer.Write('\n');
    }

    private async Task<(IPostgresChild Child, bool Adopted)> StartCoreAsync(CancellationToken cancellationToken)
    {
        var recorded = ReadPostmasterPid();
        if (recorded is { } pid && _runner.IsProcessAlive(pid.ProcessId))
        {
            if (pid.Port != _options.Port)
            {
                throw new PostgresProcessException(
                    $"The cluster in {_options.DataDirectory} is already running as pid {pid.ProcessId} on port {pid.Port}, " +
                    $"not the configured port {_options.Port}. Refusing to start a second server.");
            }

            var attached = _runner.Attach(pid.ProcessId);
            await WaitForReadinessAsync(attached, cancellationToken);
            return (attached, true);
        }

        if (await IsAcceptingAsync(cancellationToken))
        {
            throw new PostgresProcessException(
                $"Port {_options.Port} on {_options.ListenAddress} is held by another process that is not this PostgreSQL cluster. Refusing to start.");
        }

        var arguments = new[]
        {
            "-D", _options.DataDirectory,
            "-p", _options.Port.ToString(CultureInfo.InvariantCulture),
            "-h", _options.ListenAddress,
        };
        var child = _runner.Start(ToolPath("postgres"), arguments);
        try
        {
            await WaitForReadinessAsync(child, cancellationToken);
        }
        catch
        {
            child.Kill();
            child.Dispose();
            throw;
        }

        return (child, false);
    }

    private async Task WaitForReadinessAsync(IPostgresChild child, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.StartupTimeout);

        try
        {
            while (true)
            {
                if (child.HasExited)
                {
                    throw new PostgresProcessException($"PostgreSQL exited before it accepted connections on port {_options.Port}. See the server log in {_options.DataDirectory}.");
                }

                if (await IsAcceptingAsync(deadline.Token))
                {
                    return;
                }

                await Task.Delay(ReadinessPollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PostgresProcessException($"PostgreSQL did not accept connections on port {_options.Port} within {_options.StartupTimeout}.");
        }
    }

    private async Task<bool> IsAcceptingAsync(CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(ProbeAttemptTimeout);
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(ProbeHost, _options.Port, attempt.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task ShutDownAsync(IPostgresChild child, CancellationToken cancellationToken)
    {
        await RequestShutdownAsync("fast", cancellationToken);
        if (await WaitForExitWithinAsync(child, _options.ShutdownTimeout, cancellationToken))
        {
            return;
        }

        Raise(PostgresProcessEventKind.ForcedShutdown, $"Fast shutdown did not finish within {_options.ShutdownTimeout}; requesting an immediate shutdown.");
        await RequestShutdownAsync("immediate", cancellationToken);
        if (await WaitForExitWithinAsync(child, _options.ShutdownTimeout, cancellationToken))
        {
            return;
        }

        child.Kill();
    }

    private async Task RequestShutdownAsync(string mode, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = ["stop", "-D", _options.DataDirectory, "-m", mode, "-s"];
        var result = await _runner.RunAsync(ToolPath("pg_ctl"), arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            Raise(PostgresProcessEventKind.ShutdownRequestFailed, $"pg_ctl stop -m {mode} exited with code {result.ExitCode}.");
        }
    }

    private static async Task<bool> WaitForExitWithinAsync(IPostgresChild child, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await child.WaitForExitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SuperviseAsync(IPostgresChild initial, CancellationToken cancellationToken)
    {
        IPostgresChild? current = initial;
        var failures = 0;
        var runStarted = Stopwatch.GetTimestamp();

        while (true)
        {
            if (current is not null)
            {
                int exitCode;
                try
                {
                    exitCode = await current.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_stopRequested)
                {
                    return;
                }

                if (Stopwatch.GetElapsedTime(runStarted) >= _options.RestartResetAfter)
                {
                    failures = 0;
                }

                current = null;
                _state = PostgresProcessState.Restarting;
                Raise(PostgresProcessEventKind.Exited, $"postgres exited unexpectedly (exit code {exitCode}).", exitCode: exitCode);
            }

            failures++;
            if (failures > _options.MaxRestartAttempts)
            {
                _state = PostgresProcessState.Failed;
                Raise(
                    PostgresProcessEventKind.RestartsExhausted,
                    $"postgres failed {_options.MaxRestartAttempts} restart attempts in a row; not restarting again.",
                    attempt: failures - 1);
                return;
            }

            var delay = PostgresRestartBackoff.DelayFor(failures, _options.RestartBaseDelay, _options.RestartMaxDelay);
            Raise(
                PostgresProcessEventKind.RestartScheduled,
                $"Restart attempt {failures} of {_options.MaxRestartAttempts} in {delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.",
                attempt: failures,
                restartDelay: delay);

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var (child, adopted) = await StartCoreAsync(cancellationToken);
                _child = child;
                _adopted = adopted;
                current = child;
                runStarted = Stopwatch.GetTimestamp();
                _state = PostgresProcessState.Running;
                Raise(PostgresProcessEventKind.Restarted, $"Restarted postgres (pid {child.ProcessId}) on attempt {failures}.", attempt: failures);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any failure (including Win32 and access errors) counts as a failed attempt; the loop retries or gives up.
                Raise(PostgresProcessEventKind.RestartAttemptFailed, ex.Message, attempt: failures);
            }
        }
    }

    private PostmasterPid? ReadPostmasterPid()
    {
        var path = Path.Combine(_options.DataDirectory, "postmaster.pid");
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length < 4)
        {
            return null;
        }

        if (!int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        return new PostmasterPid(pid, port);
    }

    private string ToolPath(string name) =>
        Path.Combine(_options.BinDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    private void Raise(
        PostgresProcessEventKind kind,
        string message,
        int? exitCode = null,
        int attempt = 0,
        TimeSpan? restartDelay = null) =>
        ProcessEvent?.Invoke(new PostgresProcessEvent(kind, message, exitCode, attempt, restartDelay));

    /// <summary>The first lines of postmaster.pid: pid on line 1, port on line 4.</summary>
    private sealed record PostmasterPid(int ProcessId, int Port);
}
