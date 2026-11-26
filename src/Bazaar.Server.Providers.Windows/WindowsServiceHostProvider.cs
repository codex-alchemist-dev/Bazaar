using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Providers.Windows;

/// <summary>
/// Windows Service host (doc 03). Runtime registration (<c>AddWindowsService</c>) lands with the host package.
/// Recovery read-back runs <c>sc.exe qfailure</c> through an injected runner, so the parsing is testable on any OS.
/// </summary>
public sealed partial class WindowsServiceHostProvider : IServiceHostProvider
{
    /// <summary>The kind name a <c>host.provider.service_host</c> setting selects.</summary>
    public const string KindName = "windows_service";

    /// <summary>The service name the installer registers by default.</summary>
    public const string DefaultServiceName = "Bazaar.Server";

    private const string _requiresWindows = "requires Windows";

    private readonly string _serviceName;
    private readonly Func<string, string, CancellationToken, Task<string>> _runProcess;

    /// <summary>Creates the provider for <see cref="DefaultServiceName"/>, running <c>sc.exe</c> from the system folder.</summary>
    public WindowsServiceHostProvider()
        : this(DefaultServiceName, RunProcessAsync)
    {
    }

    /// <summary>
    /// Creates the provider for a named service. <paramref name="runProcess"/> receives a file name and an argument string
    /// and returns the standard output; it is the only place a process starts.
    /// </summary>
    public WindowsServiceHostProvider(string serviceName, Func<string, string, CancellationToken, Task<string>> runProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(runProcess);

        // The name goes into a command line, so only the characters a service name may contain are accepted.
        if (!serviceName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new ArgumentException("Service names may contain only ASCII letters, digits, '.', '-' and '_'.", nameof(serviceName));
        }

        _serviceName = serviceName;
        _runProcess = runProcess;
    }

    /// <inheritdoc />
    public string Kind => KindName;

    /// <summary>Usable only on Windows. Probing reads nothing and changes nothing.</summary>
    public ProviderStatus Probe() => OperatingSystem.IsWindows()
        ? new ProviderStatus(Kind, true, [], null)
        : new ProviderStatus(Kind, false, [], _requiresWindows);

    /// <summary>
    /// Reads the failure policy with <c>sc.exe qfailure</c>. Actions holds one entry per <c>RESTART</c> action, in order,
    /// followed by the failure-count reset period when the service reports one.
    /// </summary>
    public async Task<ServiceRecoveryReport> RecoveryAsync(CancellationToken ct)
    {
        var output = await _runProcess(ScExecutable, $"qfailure {_serviceName}", ct);
        return ParseFailureOutput(output);
    }

    /// <summary>Parses the text <c>sc.exe qfailure</c> prints. Pure text work, so it runs on any OS.</summary>
    private static ServiceRecoveryReport ParseFailureOutput(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var actions = new List<string>();
        var resetSeconds = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var key = KeyOf(lines[i]);

            if (key == "RESET_PERIOD" && int.TryParse(ValueOf(lines[i]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                resetSeconds = seconds;
            }

            if (key != "FAILURE_ACTIONS")
            {
                continue;
            }

            var candidates = new List<string>();
            var first = ValueOf(lines[i]);
            if (first.Length > 0)
            {
                candidates.Add(first);
            }

            // Continuation lines are indented and start with the action name; a new key ends the block.
            for (var j = i + 1; j < lines.Length && IsActionLine(lines[j]); j++)
            {
                candidates.Add(lines[j].Trim());
            }

            foreach (var candidate in candidates.Where(c => c.StartsWith("RESTART", StringComparison.Ordinal)))
            {
                actions.Add(DescribeRestart(candidate));
            }
        }

        if (actions.Count == 0)
        {
            return new ServiceRecoveryReport(false, []);
        }

        if (resetSeconds > 0)
        {
            actions.Add(string.Create(CultureInfo.InvariantCulture, $"failure count resets after {resetSeconds} s"));
        }

        return new ServiceRecoveryReport(true, actions);
    }

    private static string KeyOf(string line)
    {
        var trimmed = line.Trim();
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? string.Empty : trimmed[..colon].TrimEnd().Split(' ')[0];
    }

    private static string ValueOf(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
    }

    private static bool IsActionLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("RESTART", StringComparison.Ordinal)
            || trimmed.StartsWith("REBOOT", StringComparison.Ordinal)
            || trimmed.StartsWith("RUN", StringComparison.Ordinal)
            || trimmed.StartsWith("NONE", StringComparison.Ordinal);
    }

    private static string DescribeRestart(string action)
    {
        var match = DelayPattern().Match(action);
        return match.Success
            ? string.Create(CultureInfo.InvariantCulture, $"restart after {match.Groups[1].Value} ms")
            : "restart";
    }

    [GeneratedRegex(@"Delay\s*=\s*(\d+)")]
    private static partial Regex DelayPattern();

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("sc.exe requires Windows.");
        }

        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("sc.exe could not be started.");

        // sc.exe prints its errors on standard output too, so the text is returned whatever the exit code.
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    private static string ScExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
}
