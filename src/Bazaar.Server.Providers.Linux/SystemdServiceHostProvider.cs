using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.Providers.Linux;

/// <summary>
/// The Linux service host (doc 03): the host runs as a systemd service and logs go to journald via stdout.
/// Usable only when systemd is PID 1, which is the case when <c>/run/systemd/system</c> exists.
/// <see cref="RecoveryAsync"/> reads the restart policy back with <c>systemctl show</c>.
/// </summary>
public sealed class SystemdServiceHostProvider : IServiceHostProvider
{
    /// <summary>The unit the host installs under when no other name is given.</summary>
    public const string DefaultUnitName = "bazaar-server.service";

    private const string _kind = "systemd";
    private const string _systemdRuntimeDirectory = "/run/systemd/system";
    private const string _journalSocketPath = "/run/systemd/journal/socket";
    private const string _journaldFeature = "journald";
    private const string _restartProperty = "Restart";
    private const string _restartDelayProperty = "RestartUSec";
    private const string _startLimitIntervalProperty = "StartLimitIntervalUSec";
    private const string _startLimitBurstProperty = "StartLimitBurst";
    private const string _noRestart = "no";
    private const ulong _microsecondsPerSecond = 1_000_000;
    private const ulong _microsecondsPerMinute = 60 * _microsecondsPerSecond;
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<bool> _isLinux;
    private readonly Func<string, bool> _pathExists;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> _runProcess;
    private readonly string _unitName;

    /// <summary>Creates the provider for the real machine, using the default unit name.</summary>
    public SystemdServiceHostProvider()
        : this(OperatingSystem.IsLinux, PathExists, RunProcessAsync, DefaultUnitName)
    {
    }

    /// <summary>
    /// Creates the provider with injected checks, so tests do not depend on the machine they run on.
    /// <paramref name="runProcess"/> takes a file name, its arguments and a token, and returns standard output.
    /// It throws when the process fails.
    /// </summary>
    public SystemdServiceHostProvider(
        Func<bool> isLinux,
        Func<string, bool> pathExists,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> runProcess,
        string unitName = DefaultUnitName)
    {
        ArgumentNullException.ThrowIfNull(isLinux);
        ArgumentNullException.ThrowIfNull(pathExists);
        ArgumentNullException.ThrowIfNull(runProcess);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);
        if (unitName.StartsWith('-'))
        {
            throw new ArgumentException("The unit name must not start with a dash.", nameof(unitName));
        }

        _isLinux = isLinux;
        _pathExists = pathExists;
        _runProcess = runProcess;
        _unitName = unitName;
    }

    /// <inheritdoc />
    public string Kind => _kind;

    /// <inheritdoc />
    public ProviderStatus Probe()
    {
        if (!_isLinux())
        {
            return new ProviderStatus(Kind, false, [], "requires Linux");
        }

        if (!_pathExists(_systemdRuntimeDirectory))
        {
            return new ProviderStatus(Kind, false, [], "systemd is not running");
        }

        var features = new List<string>();
        if (_pathExists(_journalSocketPath))
        {
            features.Add(_journaldFeature);
        }

        return new ProviderStatus(Kind, true, features, null);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">systemctl exited with an error, for example when it is not installed.</exception>
    public async Task<ServiceRecoveryReport> RecoveryAsync(CancellationToken ct)
    {
        var arguments = new[]
        {
            "show",
            _unitName,
            $"--property={_restartProperty},{_restartDelayProperty},{_startLimitIntervalProperty},{_startLimitBurstProperty}",
        };

        var output = await _runProcess("systemctl", arguments, ct);
        return ParseRecovery(output);
    }

    /// <summary>Turns <c>systemctl show</c> key=value output into a report. Missing keys mean no policy for that part.</summary>
    private static ServiceRecoveryReport ParseRecovery(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var restart = values.GetValueOrDefault(_restartProperty, _noRestart);
        if (restart.Length == 0 || restart == _noRestart)
        {
            return new ServiceRecoveryReport(false, []);
        }

        var action = new StringBuilder(DescribeRestart(restart));
        if (values.TryGetValue(_restartDelayProperty, out var delay) && delay.Length > 0)
        {
            action.Append(" after ").Append(FormatTime(delay));
        }

        if (values.TryGetValue(_startLimitBurstProperty, out var burst)
            && int.TryParse(burst, out var burstCount)
            && burstCount > 0)
        {
            action.Append(" (at most ").Append(burstCount).Append(" starts");
            if (values.TryGetValue(_startLimitIntervalProperty, out var interval) && interval.Length > 0)
            {
                action.Append(" within ").Append(FormatTime(interval));
            }

            action.Append(')');
        }

        return new ServiceRecoveryReport(true, [action.ToString()]);
    }

    private static string DescribeRestart(string restart) => restart switch
    {
        "always" => "restart always",
        "on-success" => "restart on clean exit",
        "on-failure" => "restart on failure",
        "on-abnormal" => "restart on abnormal exit",
        "on-abort" => "restart on abort",
        "on-watchdog" => "restart on watchdog timeout",
        _ => $"restart ({restart})",
    };

    /// <summary>Shows a systemd time span readably. Plain microsecond counts are converted; anything else is shown as given.</summary>
    private static string FormatTime(string value)
    {
        if (!ulong.TryParse(value, out var microseconds))
        {
            return value;
        }

        if (microseconds != 0 && microseconds % _microsecondsPerMinute == 0)
        {
            return $"{microseconds / _microsecondsPerMinute}min";
        }

        if (microseconds != 0 && microseconds % _microsecondsPerSecond == 0)
        {
            return $"{microseconds / _microsecondsPerSecond}s";
        }

        return $"{microseconds}us";
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = new CancellationTokenSource(_processTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException($"'{fileName}' did not start.");
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"'{fileName}' exited with code {process.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new TimeoutException($"'{fileName}' did not finish within {_processTimeout.TotalSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }
}
