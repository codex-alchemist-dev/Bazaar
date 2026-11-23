using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>Result of one process execution by the self-test.</summary>
internal sealed record LinuxProbeOutput(int ExitCode, string Stdout, string Stderr);

/// <summary>Seam for running the probe, so tests can inject a runner that fails or lies.</summary>
internal interface ILinuxProbeRunner
{
    Task<LinuxProbeOutput> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The self-test outcome. <see cref="Passed"/> is true only when every check proved its property on this host.</summary>
/// <param name="Passed">Every check that applies passed (the four namespace checks, and the hardening checks when a helper is configured).</param>
/// <param name="Reasons">One entry per failed check or missing tool; empty when passed.</param>
/// <param name="HardeningLevel">What was proved: <c>namespaces+rlimits</c>, or <c>namespaces+rlimits+landlock+seccomp</c> only when every hardening check passed.</param>
public sealed record LinuxConfinementSelfTestResult(bool Passed, IReadOnlyList<string> Reasons, string HardeningLevel = LinuxConfinementSelfTest.BaseLevel)
{
    /// <summary>The reasons joined into one string, or null when passed.</summary>
    public string? Summary => Passed ? null : string.Join("; ", Reasons);
}

/// <summary>
/// Proves on this host, by actually launching a probe through the same command line the provider uses, that: (a) unprivileged
/// <c>unshare</c> with the namespace flags works; (b) the process sees no network interface except loopback; (c) it is pid 1 of its own
/// pid namespace; (d) the address-space limit from <c>prlimit</c> is applied. Any failure, missing tool or error fails the test.
/// </summary>
public static class LinuxConfinementSelfTest
{
    /// <summary>The hardening level of namespaces and rlimits alone.</summary>
    public const string BaseLevel = "namespaces+rlimits";

    /// <summary>The hardening level when the helper's Landlock and seccomp checks all passed.</summary>
    public const string HardenedLevel = "namespaces+rlimits+landlock+seccomp";

    private const int _probeMemoryMb = 256;
    private const string _canaryText = "bazaar-canary-must-stay-unreadable";

    // Hardened probe, shell builtins only. $1 = canary directory (outside every allowed path), $2 = shell, $3 = work directory.
    private const string _hardenedProbeScript =
        "CANARY=$1; SHELLPATH=$2; cd \"$3\" || exit 9; " +
        "echo \"PID=$$\"; n=0; while IFS=: read -r name rest; do n=$((n+1)); if [ \"$n\" -gt 2 ]; then set -- $name; echo \"IF=$1\"; fi; done < /proc/net/dev; " +
        "while read -r a b c d e f; do if [ \"$a $b $c\" = \"Max address space\" ]; then echo \"AS=$d,$e\"; fi; done < /proc/self/limits; " +
        "while read -r k v; do case $k in NoNewPrivs:) echo \"NNP=$v\";; Seccomp:) echo \"SECCOMP=$v\";; esac; done < /proc/self/status; " +
        "r=$( ( read -r x < \"$CANARY/canary.txt\" && echo \"$x\" ) 2>&1 ); case \"$r\" in *ermission\\ denied*) echo OUTSIDE_READ=denied;; *) echo \"OUTSIDE_READ=other:$r\";; esac; " +
        "w=$( ( echo leak > \"$CANARY/written.txt\" ) 2>&1 ); case \"$w\" in *ermission\\ denied*) echo OUTSIDE_WRITE=denied;; *) echo \"OUTSIDE_WRITE=other:$w\";; esac; " +
        "if echo ok > ./probe.txt && read -r y < ./probe.txt; then echo \"WORKDIR=$y\"; else echo WORKDIR=failed; fi; " +
        "\"$SHELLPATH\" -c 'echo NESTED=ok'";

    // Builtins only, so the probe needs nothing but the shell. Prints PID, interface names and the address-space soft,hard limits.
    private const string _probeScript =
        "echo \"PID=$$\"; n=0; while IFS=: read -r name rest; do n=$((n+1)); if [ \"$n\" -gt 2 ]; then set -- $name; echo \"IF=$1\"; fi; done < /proc/net/dev; " +
        "while read -r a b c d e f; do if [ \"$a $b $c\" = \"Max address space\" ]; then echo \"AS=$d,$e\"; fi; done < /proc/self/limits";

    /// <summary>Runs the self-test with the real process runner.</summary>
    public static Task<LinuxConfinementSelfTestResult> RunAsync(LinuxConfinementOptions options, CancellationToken ct = default) =>
        OperatingSystem.IsLinux()
            ? RunAsync(options, new ProcessProbeRunner(options), ct)
            : Task.FromResult(new LinuxConfinementSelfTestResult(false, ["host is not Linux"]));

    internal static async Task<LinuxConfinementSelfTestResult> RunAsync(LinuxConfinementOptions options, ILinuxProbeRunner runner, CancellationToken ct)
    {
        var reasons = new List<string>();
        var unshare = options.ResolveUnshare();
        var prlimit = options.ResolvePrlimit();
        if (unshare is null)
        {
            reasons.Add("unshare (util-linux) not found");
        }

        if (prlimit is null)
        {
            reasons.Add("prlimit (util-linux) not found");
        }

        if (!File.Exists(options.ShellPath))
        {
            reasons.Add($"shell not found at {options.ShellPath}");
        }

        if (reasons.Count > 0)
        {
            return new(false, reasons);
        }

        if (options.HelperPath is not null)
        {
#pragma warning disable CA1416 // The public entry point refuses non-Linux hosts; the internal seam is exercised with a scripted runner on any OS.
            return await RunHardenedAsync(options, runner, unshare!, prlimit!, ct).ConfigureAwait(false);
#pragma warning restore CA1416
        }

        var command = new LinuxLaunchCommand(options.ShellPath, ["-c", _probeScript]);
        var args = LinuxCommandLine.BuildArguments(options, unshare!, _probeMemoryMb, 60, command);
        LinuxProbeOutput output;
        try
        {
            output = await runner.RunAsync(prlimit!, args, options.SelfTestTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, [$"probe could not run: {ex.GetType().Name}: {ex.Message}"]);
        }

        if (output.ExitCode != 0)
        {
            return new(false, [$"(a) unshare with user/net/pid/mount/ipc/uts namespaces failed (exit {output.ExitCode}): {Truncate(output.Stderr)}"]);
        }

        CheckNamespaceOutput(output.Stdout, reasons);
        return new(reasons.Count == 0, reasons);
    }

    /// <summary>
    /// The hardened variant: the same namespace checks, run through the helper (so also proving the helper starts under the real wrapper),
    /// plus: no_new_privs is set; a seccomp filter is active; Landlock denies reading a file and writing a file outside the allowed paths
    /// (a canary directory the probe is not allowed into); the work directory and a nested fork+exec still work; and the helper's
    /// <c>--self-test socket</c> is killed by SIGSYS when it calls socket(). Failure of any check fails the whole self-test: no downgrade.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<LinuxConfinementSelfTestResult> RunHardenedAsync(
        LinuxConfinementOptions options,
        ILinuxProbeRunner runner,
        string unshare,
        string prlimit,
        CancellationToken ct)
    {
        var helper = options.HelperPath!;
        if (!File.Exists(helper))
        {
            return new(false, [$"(e) hardening helper not found at {helper}"]);
        }

        var root = options.WorkRoot ?? Path.GetTempPath();
        var id = Guid.NewGuid().ToString("N");
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var workDir = Path.Combine(root, "bazaar-sbx-selftest-work-" + id);
        var canaryDir = Path.Combine(root, "bazaar-sbx-selftest-canary-" + id);
        Directory.CreateDirectory(workDir, OwnerOnly);
        Directory.CreateDirectory(canaryDir, OwnerOnly);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(canaryDir, "canary.txt"), _canaryText, ct).ConfigureAwait(false);
            var reasons = new List<string>();

            // Run 1: namespace checks plus the Landlock, no_new_privs and exec checks, through the helper.
            var command = new LinuxLaunchCommand(options.ShellPath, ["-c", _hardenedProbeScript, "sh", canaryDir, options.ShellPath, workDir]);
            var output = await RunThroughHelperAsync(options, runner, unshare, prlimit, command, workDir, [], ct).ConfigureAwait(false);
            if (output.Failure is not null)
            {
                return new(false, [output.Failure]);
            }

            var probe = output.Result!;
            if (probe.ExitCode != 0)
            {
                return new(false, [$"(a) unshare or the hardening helper failed (exit {probe.ExitCode}): {Truncate(probe.Stderr)}"]);
            }

            CheckNamespaceOutput(probe.Stdout, reasons);
            var values = ParseValues(probe.Stdout);
            Expect(values, "NNP", "1", "(f) no_new_privs is not set in the confined process", reasons);
            Expect(values, "SECCOMP", "2", "(g) no seccomp filter is active in the confined process", reasons);
            Expect(values, "OUTSIDE_READ", "denied", "(h) Landlock did not deny reading a file outside the allowed paths", reasons);
            Expect(values, "OUTSIDE_WRITE", "denied", "(i) Landlock did not deny writing a file outside the work directory", reasons);
            Expect(values, "WORKDIR", "ok", "(j) the work directory is not readable and writable under Landlock", reasons);
            Expect(values, "NESTED", "ok", "(k) a nested fork and exec does not work under the filter", reasons);
            if (probe.Stdout.Contains(_canaryText, StringComparison.Ordinal))
            {
                reasons.Add("(h) the canary text outside the allowed paths was readable");
            }

            if (File.Exists(Path.Combine(canaryDir, "written.txt")))
            {
                reasons.Add("(i) a file was created outside the work directory");
            }

            // Run 2: a denied syscall must kill the process (SIGSYS, seen as exit 159 through the wrapper).
            var socketCommand = new LinuxLaunchCommand(options.ShellPath, []);
            var socket = await RunThroughHelperAsync(options, runner, unshare, prlimit, socketCommand, workDir, ["--self-test", "socket"], ct).ConfigureAwait(false);
            if (socket.Failure is not null)
            {
                reasons.Add(socket.Failure);
            }
            else
            {
                var s = socket.Result!;
                if (s.Stderr.Contains("SOCKET_ALLOWED", StringComparison.Ordinal) || s.ExitCode == 3)
                {
                    reasons.Add("(l) seccomp did not stop a socket() call");
                }
                else if (s.ExitCode != 128 + 31 || !s.Stderr.Contains("ready", StringComparison.Ordinal))
                {
                    reasons.Add($"(l) the socket() probe did not end with SIGSYS after the filter was installed (exit {s.ExitCode}): {Truncate(s.Stderr)}");
                }
            }

            return new(reasons.Count == 0, reasons, reasons.Count == 0 ? HardenedLevel : BaseLevel);
        }
        finally
        {
            TryDelete(workDir);
            TryDelete(canaryDir);
        }
    }

    private static async Task<(LinuxProbeOutput? Result, string? Failure)> RunThroughHelperAsync(
        LinuxConfinementOptions options,
        ILinuxProbeRunner runner,
        string unshare,
        string prlimit,
        LinuxLaunchCommand command,
        string workDir,
        IReadOnlyList<string> helperArguments,
        CancellationToken ct)
    {
        var policyFile = Path.Combine(workDir, ".bazaar-policy.json");
        var policy = LinuxHardeningPolicy.Create(options, command, workDir, (long)_probeMemoryMb * 1024 * 1024);
        await File.WriteAllTextAsync(policyFile, LinuxHardeningPolicy.Serialize(policy), ct).ConfigureAwait(false);
        var args = LinuxCommandLine.BuildArguments(options, unshare, _probeMemoryMb, 60, command, new LinuxHardeningLaunch(options.HelperPath!, policyFile, helperArguments));
        try
        {
            return (await runner.RunAsync(prlimit, args, options.SelfTestTimeout, ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"probe could not run: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseValues(string stdout)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                values[line[..eq]] = line[(eq + 1)..];
            }
        }

        return values;
    }

    private static void Expect(Dictionary<string, string> values, string key, string expected, string reason, List<string> reasons)
    {
        if (!values.TryGetValue(key, out var actual) || actual != expected)
        {
            reasons.Add($"{reason} (saw {(values.TryGetValue(key, out var seen) ? Truncate(seen) : "nothing")})");
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CheckNamespaceOutput(string stdout, List<string> reasons)
    {
        string? pid = null;
        string? limit = null;
        var interfaces = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("PID=", StringComparison.Ordinal))
            {
                pid = line[4..];
            }
            else if (line.StartsWith("IF=", StringComparison.Ordinal))
            {
                interfaces.Add(line[3..]);
            }
            else if (line.StartsWith("AS=", StringComparison.Ordinal))
            {
                limit = line[3..];
            }
        }

        if (interfaces.Count != 1 || interfaces[0] != "lo")
        {
            reasons.Add($"(b) network not isolated: interfaces seen = [{string.Join(",", interfaces)}], expected only lo");
        }

        if (pid != "1")
        {
            reasons.Add($"(c) pid namespace not in effect: probe pid = {pid ?? "(none)"}, expected 1");
        }

        var expected = ((long)_probeMemoryMb * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
        if (limit != expected + "," + expected)
        {
            reasons.Add($"(d) address-space limit not applied: saw {limit ?? "(none)"}, expected {expected},{expected}");
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();

    private sealed class ProcessProbeRunner(LinuxConfinementOptions options) : ILinuxProbeRunner
    {
        public async Task<LinuxProbeOutput> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            var psi = LinuxProcessFactory.CreateStartInfo(options, file, arguments, workingDirectory: "/");
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
            process.StandardInput.Close();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outTask = ReadAsync(process.StandardOutput, stdout);
            var errTask = ReadAsync(process.StandardError, stderr);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException("probe timed out");
            }

            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());
        }

        private static async Task ReadAsync(StreamReader reader, StringBuilder sink)
        {
            var buffer = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (sink.Length < 65536)
                {
                    sink.Append(buffer, 0, n);
                }
            }
        }
    }
}
