using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>The self-test outcome. <see cref="Passed"/> is true only when every check proved its property on this host.</summary>
/// <param name="Passed">Every check passed, through AppContainer.</param>
/// <param name="Reasons">One entry per failed check or missing tool; empty when passed.</param>
/// <param name="HardeningLevel">What was proved: <c>none</c>, <c>job+restricted-token</c> (job checks passed through the weaker fallback; never tested), or <c>job+appcontainer</c> (every check passed).</param>
public sealed record WindowsConfinementSelfTestResult(bool Passed, IReadOnlyList<string> Reasons, string HardeningLevel = WindowsConfinementSelfTest.NoLevel)
{
    /// <summary>The reasons joined into one string, or null when passed.</summary>
    public string? Summary => Passed ? null : string.Join("; ", Reasons);
}

/// <summary>
/// Proves on this host, by launching real probes through the same sequence the provider uses, that: (a) closing the job object kills
/// its processes; (b) a child of a job process is itself in the job, and the job allows no breakaway; (c) the AppContainer process
/// cannot read or write a file outside its work directory but can use the work directory; (d) it cannot open a network connection;
/// (e) the memory limit (and the active-process, CPU-time and UI limits) are held by the job. Any failure, missing tool or error fails
/// the test; a host that is not Windows reports untested and never throws.
/// </summary>
public static class WindowsConfinementSelfTest
{
    /// <summary>Nothing was proved.</summary>
    public const string NoLevel = "none";

    /// <summary>Job checks passed through the restricted-token fallback. Weaker than AppContainer; the provider stays untested.</summary>
    public const string RestrictedTokenLevel = "job+restricted-token";

    /// <summary>Every check passed through AppContainer.</summary>
    public const string AppContainerLevel = "job+appcontainer";

    private const long _probeMemoryBytes = 256L * 1024 * 1024;
    private const int _probeCpuSeconds = 120;
    private const uint _allUiRestrictions = 0xFF;
    private const string _canaryText = "bazaar-canary-must-stay-unreadable";

    /// <summary>Runs the self-test on this host with the real Win32 implementation (untested, without throwing, when the host is not Windows).</summary>
    public static Task<WindowsConfinementSelfTestResult> RunAsync(WindowsConfinementOptions options, CancellationToken ct = default) =>
        RunAsync(options, WindowsProvider.CreateRealApi(), ct);

    internal static async Task<WindowsConfinementSelfTestResult> RunAsync(WindowsConfinementOptions options, IWindowsConfinementApi? api, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            if (api is null || !api.IsWindows)
            {
                return new(false, ["host is not Windows; the Windows confinement cannot be proved here"]);
            }

            var appContainer = api.IsAppContainerSupported();
            var mode = appContainer ? WindowsConfinementMode.AppContainer : WindowsConfinementMode.RestrictedToken;
            var reasons = new List<string>();
            var cmd = options.SystemFile("cmd.exe");
            var curl = options.SystemFile("curl.exe");
            if (!api.FileExists(cmd))
            {
                reasons.Add($"cmd.exe not found at {cmd}");
            }

            if (appContainer && !api.FileExists(curl))
            {
                reasons.Add($"curl.exe not found at {curl} (needed for the network probe)");
            }

            if (reasons.Count > 0)
            {
                return new(false, reasons);
            }

            if (!appContainer)
            {
                reasons.Add("AppContainer is not available on this host; the restricted-token fallback does not deny file or network access, so confinement is not proved");
            }

            var jobProved = await ProbeJobAsync(options, api, mode, cmd, reasons, ct).ConfigureAwait(false);
            if (appContainer)
            {
                await ProbeFilesAsync(options, api, cmd, reasons, ct).ConfigureAwait(false);
                await ProbeNetworkAsync(options, api, curl, reasons, ct).ConfigureAwait(false);
            }

            var level = reasons.Count == 0 ? AppContainerLevel : jobProved && !appContainer ? RestrictedTokenLevel : NoLevel;
            return new(reasons.Count == 0, reasons, level);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, [$"self-test could not run: {ex.GetType().Name}: {ex.Message}"]);
        }
    }

    private static WindowsJobSpec ProbeSpec(WindowsConfinementOptions options) =>
        new(_probeMemoryBytes, _probeMemoryBytes, TimeSpan.FromSeconds(_probeCpuSeconds), options.MaxProcesses);

    private static async Task<bool> ProbeJobAsync(
        WindowsConfinementOptions options, IWindowsConfinementApi api, WindowsConfinementMode mode, string cmd, List<string> reasons, CancellationToken ct)
    {
        var before = reasons.Count;
        WindowsSandboxProcess process;
        try
        {
            process = WindowsLauncher.Launch(api, options, "selftest-job", ProbeSpec(options), mode, work =>
            {
                // spin.cmd starts a child cmd.exe that spins (a child process to find in the job, and a process that never exits by itself).
                File.WriteAllText(Path.Combine(work, "spin.cmd"), "@cmd /d /c spin2.cmd\r\n");
                File.WriteAllText(Path.Combine(work, "spin2.cmd"), "@for /l %%i in (1,1,2000000000) do @rem\r\n");
                return new WindowsLaunchCommand(cmd, ["/d", "/c", "spin.cmd"]);
            }, allowShell: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reasons.Add($"job probe could not start: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        await using (process.ConfigureAwait(false))
        {
            var deadline = DateTime.UtcNow + options.SelfTestTimeout;
            var ids = process.Job.ProcessIds();
            while (ids.Count < 2 && !process.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                ids = process.Job.ProcessIds();
            }

            if (process.HasExited)
            {
                reasons.Add($"job probe exited early (exit {process.ExitCode}): {Truncate(process.Stderr)}");
                return false;
            }

            var limits = process.Job.QueryLimits();
            if (ids.Count < 2)
            {
                reasons.Add("(b) a child process of the job was not found in the job");
            }

            if (limits.BreakawayAllowed)
            {
                reasons.Add("(b) the job allows breakaway, so a child could run outside it");
            }

            if (!limits.KillOnJobClose)
            {
                reasons.Add("(a) the job does not have kill-on-close set");
            }

            if (limits.ProcessMemoryBytes != _probeMemoryBytes || limits.JobMemoryBytes != _probeMemoryBytes)
            {
                reasons.Add($"(e) memory limit not applied (process {limits.ProcessMemoryBytes}, job {limits.JobMemoryBytes}, expected {_probeMemoryBytes})");
            }

            if (limits.ActiveProcessLimit != options.MaxProcesses)
            {
                reasons.Add($"(e) active process limit {limits.ActiveProcessLimit} is not the requested {options.MaxProcesses}");
            }

            if (limits.CpuTime != TimeSpan.FromSeconds(_probeCpuSeconds))
            {
                reasons.Add($"(e) CPU time limit {limits.CpuTime} is not the requested {_probeCpuSeconds}s");
            }

            if (limits.UiRestrictions != _allUiRestrictions)
            {
                reasons.Add($"(e) UI restrictions 0x{limits.UiRestrictions:X} are not all set (0x{_allUiRestrictions:X})");
            }

            // Kill-on-close: close the job handle only (no terminate) and require the processes to die.
            process.Job.Dispose();
            if (!await process.Process.WaitForExitAsync(options.SelfTestTimeout, ct).ConfigureAwait(false))
            {
                reasons.Add("(a) closing the job object did not kill its process");
            }
        }

        return reasons.Count == before;
    }

    private static async Task ProbeFilesAsync(WindowsConfinementOptions options, IWindowsConfinementApi api, string cmd, List<string> reasons, CancellationToken ct)
    {
        var canaryDir = Path.Combine(options.ResolvedWorkRoot, "bazaar-sbx-canary-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(canaryDir);
            var canaryFile = Path.Combine(canaryDir, "canary.txt");
            var writtenFile = Path.Combine(canaryDir, "written.txt");
            File.WriteAllText(canaryFile, _canaryText);

            WindowsSandboxProcess process;
            string? workDir = null;
            try
            {
                process = WindowsLauncher.Launch(api, options, "selftest-files", ProbeSpec(options), WindowsConfinementMode.AppContainer, work =>
                {
                    workDir = work;
                    var script =
                        "@echo off\r\n" +
                        "echo PROBE=started\r\n" +
                        $"type \"{canaryFile}\"\r\n" +
                        "if errorlevel 1 (echo OUTSIDE_READ=denied) else (echo OUTSIDE_READ=read)\r\n" +
                        $"echo leak> \"{writtenFile}\"\r\n" +
                        "echo ok> probe.txt\r\n" +
                        "type probe.txt\r\n";
                    File.WriteAllText(Path.Combine(work, "files.cmd"), script);
                    return new WindowsLaunchCommand(cmd, ["/d", "/c", "files.cmd"]);
                }, allowShell: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reasons.Add($"file probe could not start: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            await using (process.ConfigureAwait(false))
            {
                if (!await process.Process.WaitForExitAsync(options.SelfTestTimeout, ct).ConfigureAwait(false))
                {
                    reasons.Add("(c) file probe did not finish");
                    return;
                }

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var output = process.Stdout;
                if (!output.Contains("PROBE=started", StringComparison.Ordinal))
                {
                    reasons.Add($"(c) file probe script did not run inside the AppContainer: {Truncate(process.Stderr)}");
                    return;
                }

                if (output.Contains(_canaryText, StringComparison.Ordinal) || !output.Contains("OUTSIDE_READ=denied", StringComparison.Ordinal))
                {
                    reasons.Add("(c) the AppContainer process could read a file outside its work directory");
                }

                if (File.Exists(writtenFile))
                {
                    reasons.Add("(c) the AppContainer process could write a file outside its work directory");
                }

                var probeFile = workDir is null ? null : Path.Combine(workDir, "probe.txt");
                if (probeFile is null || !File.Exists(probeFile) || !output.Contains("ok", StringComparison.Ordinal))
                {
                    reasons.Add("(c) the AppContainer process could not write inside its own work directory");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(canaryDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftover canary directory; not a security result.
            }
        }
    }

    private static async Task ProbeNetworkAsync(WindowsConfinementOptions options, IWindowsConfinementApi api, string curl, List<string> reasons, CancellationToken ct)
    {
        var address = FindNonLoopbackAddress() ?? IPAddress.Loopback;
        using var listener = new TcpListener(address.Equals(IPAddress.Loopback) ? IPAddress.Loopback : IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Control: an unconfined connection from this host must reach the listener, otherwise a failed probe would prove nothing.
        try
        {
            using var control = new TcpClient();
            await control.ConnectAsync(address, port, ct).ConfigureAwait(false);
            using var accepted = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            reasons.Add($"(d) network probe control failed (the host cannot reach its own listener: {ex.Message}); denial cannot be proved");
            return;
        }

        WindowsSandboxProcess process;
        try
        {
            process = WindowsLauncher.Launch(
                api,
                options,
                "selftest-net",
                ProbeSpec(options),
                WindowsConfinementMode.AppContainer,
                _ => new WindowsLaunchCommand(curl, ["--silent", "--connect-timeout", "5", "--max-time", "8", "-o", "NUL", string.Create(CultureInfo.InvariantCulture, $"http://{address}:{port}/")]),
                allowShell: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reasons.Add($"network probe could not start: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        await using (process.ConfigureAwait(false))
        {
            if (!await process.Process.WaitForExitAsync(options.SelfTestTimeout, ct).ConfigureAwait(false))
            {
                reasons.Add("(d) network probe did not finish");
                return;
            }

            // curl 7 = could not connect, 28 = timed out. Anything else (including 0 or a start-up failure) proves nothing.
            var exit = process.ExitCode;
            if (listener.Pending())
            {
                reasons.Add("(d) the AppContainer process opened a network connection");
            }
            else if (exit is not (7 or 28))
            {
                reasons.Add($"(d) network probe ended with exit code {exit}, not a refused connection (7) or timeout (28): {Truncate(process.Stderr)}");
            }
        }
    }

    private static IPAddress? FindNonLoopbackAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        return ua.Address;
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to loopback.
        }

        return null;
    }

    private static string Truncate(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "...";
}
