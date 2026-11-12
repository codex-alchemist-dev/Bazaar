using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Runs one managed PostgreSQL cluster as a child process: initdb, start with adopt-or-refuse, stop, and restart
/// with bounded backoff after unexpected exits. Reports through <see cref="ProcessEvent"/>; handlers must not throw.
/// </summary>
public sealed class PostgresProcessController : IAsyncDisposable
{
    private const int _passwordByteCount = 32;
    private const string _redactedText = "[redacted]";
    private static readonly TimeSpan _probeAttemptTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _readinessPollInterval = TimeSpan.FromMilliseconds(100);

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
    /// Creates the cluster. A random superuser password is generated and handed to <paramref name="persistSuperuserPassword"/>
    /// before anything else runs. The bootstrap then runs initdb with trust rules, starts a bootstrap server reachable only
    /// locally, sets the password over SQL on stdin, and rewrites pg_hba.conf to scram-sha-256. The password never appears in
    /// arguments, files, events or exceptions. Until the bootstrap finishes, a pending marker blocks <see cref="StartAsync"/>.
    /// </summary>
    public async Task InitDbAsync(Func<string, CancellationToken, ValueTask> persistSuperuserPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistSuperuserPassword);
        await InitDbCoreAsync(null, persistSuperuserPassword, cancellationToken);
    }

    /// <summary>
    /// initdb with a superuser password the caller already holds. A major upgrade uses this for the new cluster: pg_upgrade
    /// connects to both clusters with one password, and the role data (including this password) is carried over from the old
    /// cluster when it runs, so the new cluster ends up with the same credentials it had before.
    /// </summary>
    public Task InitDbWithPasswordAsync(string superuserPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(superuserPassword);
        return InitDbCoreAsync(superuserPassword, null, cancellationToken);
    }

    private async Task InitDbCoreAsync(string? existingPassword, Func<string, CancellationToken, ValueTask>? persistSuperuserPassword, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(ClusterMarkerPath))
            {
                throw new PostgresProcessException($"The data directory {_options.DataDirectory} already holds a cluster; initdb runs once.");
            }

            var password = existingPassword ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(_passwordByteCount));
            if (persistSuperuserPassword is not null)
            {
                await persistSuperuserPassword(password, cancellationToken);
            }

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

            if (File.Exists(BootstrapPendingPath))
            {
                throw new PostgresProcessException(
                    $"The bootstrap of {_options.DataDirectory} did not finish, so the cluster may still trust local connections without a password. " +
                    "Remove the data directory and the marker next to it, then run InitDbAsync again.");
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

    private string BootstrapPendingPath =>
        Path.GetFullPath(_options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".bootstrap-pending";

    private async Task RunInitDbAsync(string password, CancellationToken cancellationToken)
    {
        // The marker sits beside the data directory, not inside it, because initdb refuses a non-empty directory.
        var parent = Path.GetDirectoryName(BootstrapPendingPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(BootstrapPendingPath, "Bootstrap in progress: trust rules may be active. Do not start this cluster.\n", cancellationToken);

        // Trust is only for the bootstrap. The password is set over this trust, then pg_hba is switched to scram-sha-256.
        IReadOnlyList<string> arguments =
        [
            "-D", _options.DataDirectory,
            "-U", _options.SuperuserName,
            "-A", "trust",
            "-E", "UTF8",
        ];

        var result = await _runner.RunAsync(ToolPath("initdb"), arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PostgresProcessException(
                $"initdb failed with exit code {result.ExitCode}: {result.Output.Replace(password, _redactedText, StringComparison.Ordinal)}");
        }

        await SetPasswordThenRequireScramAsync(password, cancellationToken);
        File.Delete(BootstrapPendingPath);
    }

    /// <summary>
    /// Starts a bootstrap server that accepts only local connections, sets the superuser password over stdin, stops the server,
    /// and switches pg_hba.conf from trust to scram-sha-256. On Linux the server listens on a private unix socket directory only.
    /// On Windows it listens on loopback at a free port, and the data directory is restricted to the current user.
    /// </summary>
    private async Task SetPasswordThenRequireScramAsync(string password, CancellationToken cancellationToken)
    {
        var socketDirectory = OperatingSystem.IsWindows() ? null : CreatePrivateSocketDirectory();
        var port = socketDirectory is null ? _options.Port : FreeLoopbackPort();
        var host = socketDirectory ?? "127.0.0.1";
        IPostgresChild? child = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictDataDirectoryToCurrentUser(_options.DataDirectory);
            }

            IReadOnlyList<string> serverArguments = socketDirectory is null
                ? ["-D", _options.DataDirectory, "-p", port.ToString(CultureInfo.InvariantCulture), "-c", "listen_addresses=127.0.0.1"]
                : ["-D", _options.DataDirectory, "-p", port.ToString(CultureInfo.InvariantCulture), "-c", "listen_addresses=", "-c", $"unix_socket_directories={socketDirectory}"];
            child = _runner.Start(ToolPath("postgres"), serverArguments);

            await WaitForBootstrapServerAsync(child, host, port, cancellationToken);

            var sql = $"ALTER ROLE {QuoteIdentifier(_options.SuperuserName)} PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}';\n";
            var set = await _runner.RunAsync(ToolPath("psql"), PsqlArguments(host, port, "postgres", "-f", "-"), cancellationToken, standardInput: sql);
            if (set.ExitCode != 0)
            {
                throw new PostgresProcessException(
                    $"Setting the superuser password failed with exit code {set.ExitCode}: {set.Output.Replace(password, _redactedText, StringComparison.Ordinal)}");
            }

            var stop = await _runner.RunAsync(ToolPath("pg_ctl"), ["stop", "-D", _options.DataDirectory, "-m", "fast", "-s", "-w"], cancellationToken);
            if (stop.ExitCode != 0)
            {
                throw new PostgresProcessException($"Stopping the bootstrap server failed with exit code {stop.ExitCode}.");
            }

            child.Dispose();
            child = null;
        }
        finally
        {
            if (child is not null)
            {
                child.Kill();
                child.Dispose();
            }

            if (socketDirectory is not null)
            {
                TryDeleteDirectory(socketDirectory);
            }
        }

        await SwitchPgHbaToScramAsync(cancellationToken);
    }

    private async Task SwitchPgHbaToScramAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.DataDirectory, "pg_hba.conf");
        if (!File.Exists(path))
        {
            throw new PostgresProcessException($"initdb did not write {path}; the cluster was not secured.");
        }

        var secured = PgHbaRules.SwitchTrustToScram(await File.ReadAllTextAsync(path, cancellationToken));
        if (PgHbaRules.HasTrustRule(secured))
        {
            throw new PostgresProcessException($"pg_hba.conf still has a trust rule after switching to {PgHbaRules.Scram}.");
        }

        await File.WriteAllTextAsync(path, secured, cancellationToken);
    }

    private async Task WaitForBootstrapServerAsync(IPostgresChild child, string host, int port, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.StartupTimeout);

        try
        {
            while (true)
            {
                if (child.HasExited)
                {
                    throw new PostgresProcessException("The bootstrap server exited before it accepted connections. See the server log in the data directory.");
                }

                var probe = await _runner.RunAsync(ToolPath("psql"), [.. PsqlArguments(host, port, "postgres"), "-c", "SELECT 1"], deadline.Token);
                if (probe.ExitCode == 0)
                {
                    return;
                }

                await Task.Delay(_readinessPollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PostgresProcessException($"The bootstrap server did not accept connections within {_options.StartupTimeout}.");
        }
    }

    private List<string> PsqlArguments(string host, int port, string database, params string[] extra)
    {
        var arguments = new List<string>
        {
            "-X", "-q", "-v", "ON_ERROR_STOP=1",
            "-h", host,
            "-p", port.ToString(CultureInfo.InvariantCulture),
            "-U", _options.SuperuserName,
            "-d", database,
        };
        arguments.AddRange(extra);
        return arguments;
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string CreatePrivateSocketDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-pgboot-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the temp directory is harmless once the server is gone.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: the temp directory is harmless once the server is gone.
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictDataDirectoryToCurrentUser(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
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

                await Task.Delay(_readinessPollInterval, deadline.Token);
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
        attempt.CancelAfter(_probeAttemptTimeout);
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
