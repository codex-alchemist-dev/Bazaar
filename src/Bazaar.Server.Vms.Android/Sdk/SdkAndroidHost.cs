using System.Globalization;
using System.Text;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.Android.Sdk;

/// <summary>Time limits of the SDK adapter. Safe shipped defaults.</summary>
public sealed record SdkAndroidTimeouts
{
    public TimeSpan Command { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan CreateAvd { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan Install { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan GracefulStop { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// <see cref="IAndroidHost"/> over avdmanager, emulator and adb (doc 62 section 5.1). Every child gets exactly the three Android
/// folder variables, derived from <see cref="AndroidOptions"/>, plus a private adb server port per emulator. Nothing is inherited.
/// </summary>
public sealed class SdkAndroidHost : IAndroidHost
{
    /// <summary>Device profile for new AVDs.</summary>
    public const string DefaultDevice = "pixel_6";

    private static readonly byte[] _pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int _adbPortBase = 15037;
    private const int _maxErrorText = 500;

    private readonly AndroidOptions _options;
    private readonly IAndroidCommandRunner _runner;
    private readonly SdkAndroidTimeouts _timeouts;
    private readonly AndroidSdkPaths _paths;
    private readonly Dictionary<int, IAndroidProcess> _emulators = [];
    private readonly Lock _gate = new();

    public SdkAndroidHost(AndroidOptions options, IAndroidCommandRunner runner, SdkAndroidTimeouts? timeouts = null, bool? windows = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        options.Validate();
        _options = options;
        _runner = runner;
        _timeouts = timeouts ?? new SdkAndroidTimeouts();
        _paths = new AndroidSdkPaths(options.SdkRoot, windows ?? OperatingSystem.IsWindows());
    }

    /// <summary>The private adb server port for one emulator console port, on loopback.</summary>
    public static int AdbServerPort(int consolePort)
    {
        _ = AndroidArgv.Serial(consolePort);
        return _adbPortBase + ((consolePort - 5554) / 2);
    }

    public async Task CreateAvdAsync(string avdName, string systemImagePackage, CancellationToken ct)
    {
        var args = AndroidArgv.CreateAvd(avdName, systemImagePackage, DefaultDevice);

        // "no" answers the custom hardware profile prompt.
        await RunCheckedAsync(_paths.AvdManager, args, null, _timeouts.CreateAvd, Encoding.ASCII.GetBytes("no\n"), ct).ConfigureAwait(false);
    }

    public async Task DeleteAvdAsync(string avdName, CancellationToken ct)
    {
        IReadOnlyList<string> args = ["delete", "avd", "--name", DeviceIdentifiers.Name(avdName)];
        await RunCheckedAsync(_paths.AvdManager, args, null, _timeouts.Command, null, ct).ConfigureAwait(false);
    }

    public Task StartEmulatorAsync(string avdName, int port, string? loadSnapshot, CancellationToken ct)
    {
        var args = AndroidArgv.Emulator(avdName, port, loadSnapshot);
        lock (_gate)
        {
            if (_emulators.TryGetValue(port, out var existing) && !existing.HasExited)
            {
                throw new InvalidOperationException($"An emulator is already running on port {port}.");
            }

            _emulators[port] = _runner.StartDetached(Command(_paths.Emulator, args, port, _timeouts.Command, null));
        }

        return Task.CompletedTask;
    }

    public async Task StopEmulatorAsync(int port, bool force, CancellationToken ct)
    {
        IAndroidProcess? process;
        lock (_gate)
        {
            _emulators.TryGetValue(port, out process);
        }

        if (process is null)
        {
            return;
        }

        if (!force && !process.HasExited)
        {
            try
            {
                await RunCheckedAsync(_paths.Adb, ["-s", AndroidArgv.Serial(port), "emu", "kill"], port, _timeouts.Command, null, ct).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(_timeouts.GracefulStop);
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        if (!process.HasExited)
        {
            process.Kill();
        }

        await StopAdbServerAsync(port, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _emulators.Remove(port);
        }

        process.Dispose();
    }

    public Task<bool> IsRunningAsync(int port, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_emulators.TryGetValue(port, out var p) && !p.HasExited);
        }
    }

    public async Task SaveSnapshotAsync(int port, string snapshotName, CancellationToken ct)
    {
        var name = DeviceIdentifiers.Name(snapshotName);
        var result = await RunCheckedAsync(_paths.Adb, ["-s", AndroidArgv.Serial(port), "emu", "avd", "snapshot", "save", name], port, _timeouts.Command, null, ct).ConfigureAwait(false);
        if (Encoding.UTF8.GetString(result.Stdout).Contains("KO", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The emulator refused to save the snapshot.");
        }
    }

    public async Task InstallAsync(int port, string apkPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apkPath) || apkPath.StartsWith('-') || apkPath.Contains('\0'))
        {
            throw new ArgumentException("APK path is not acceptable.", nameof(apkPath));
        }

        await RunCheckedAsync(_paths.Adb, AndroidArgv.Install(port, apkPath), port, _timeouts.Install, null, ct).ConfigureAwait(false);
    }

    public async Task LaunchAsync(int port, string appId, CancellationToken ct) =>
        await RunCheckedAsync(_paths.Adb, AndroidArgv.Launch(port, appId), port, _timeouts.Command, null, ct).ConfigureAwait(false);

    public async Task<byte[]> ScreencapAsync(int port, CancellationToken ct)
    {
        var result = await RunCheckedAsync(_paths.Adb, AndroidArgv.Screencap(port), port, _timeouts.Command, null, ct).ConfigureAwait(false);
        if (!IsPng(result.Stdout))
        {
            throw new InvalidOperationException("Screencap did not return a PNG image.");
        }

        return result.Stdout;
    }

    public async Task InputAsync(int port, DeviceInput input, CancellationToken ct) =>
        await RunCheckedAsync(_paths.Adb, AndroidArgv.Input(port, input), port, _timeouts.Command, null, ct).ConfigureAwait(false);

    internal static bool IsPng(byte[] data) => data.Length > _pngMagic.Length && data.AsSpan(0, _pngMagic.Length).SequenceEqual(_pngMagic);

    private AndroidCommand Command(string executable, IReadOnlyList<string> args, int? port, TimeSpan timeout, byte[]? stdin)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANDROID_HOME"] = _options.SdkRoot,
            ["ANDROID_SDK_ROOT"] = _options.SdkRoot,
            ["ANDROID_AVD_HOME"] = _options.AvdHome,
        };
        if (port is { } p)
        {
            env["ANDROID_ADB_SERVER_PORT"] = AdbServerPort(p).ToString(CultureInfo.InvariantCulture);
        }

        return new AndroidCommand(executable, args, env, timeout, stdin);
    }

    private async Task<AndroidCommandResult> RunCheckedAsync(string executable, IReadOnlyList<string> args, int? port, TimeSpan timeout, byte[]? stdin, CancellationToken ct)
    {
        var result = await _runner.RunAsync(Command(executable, args, port, timeout, stdin), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var text = result.Stderr.Length > _maxErrorText ? result.Stderr[.._maxErrorText] : result.Stderr;
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {result.ExitCode}: {text.Trim()}");
        }

        return result;
    }

    private async Task StopAdbServerAsync(int port, CancellationToken ct)
    {
        try
        {
            await _runner.RunAsync(Command(_paths.Adb, ["kill-server"], port, _timeouts.Command, null), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort: the private server holds no state and exits with its idle timeout.
        }
    }
}
