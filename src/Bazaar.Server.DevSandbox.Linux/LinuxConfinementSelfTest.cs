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
/// <param name="Passed">All four checks passed.</param>
/// <param name="Reasons">One entry per failed check or missing tool; empty when passed.</param>
public sealed record LinuxConfinementSelfTestResult(bool Passed, IReadOnlyList<string> Reasons)
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
    private const int _probeMemoryMb = 256;

    // Builtins only, so the probe needs nothing but the shell. Prints PID, interface names and the address-space soft,hard limits.
    private const string _probeScript =
        "echo \"PID=$$\"; n=0; while IFS=: read -r name rest; do n=$((n+1)); if [ \"$n\" -gt 2 ]; then set -- $name; echo \"IF=$1\"; fi; done < /proc/net/dev; " +
        "while read -r a b c d e f; do if [ \"$a $b $c\" = \"Max address space\" ]; then echo \"AS=$d,$e\"; fi; done < /proc/self/limits";

    /// <summary>Runs the self-test with the real process runner.</summary>
    public static Task<LinuxConfinementSelfTestResult> RunAsync(LinuxConfinementOptions options, CancellationToken ct = default) =>
        RunAsync(options, new ProcessProbeRunner(options), ct);

    internal static async Task<LinuxConfinementSelfTestResult> RunAsync(LinuxConfinementOptions options, ILinuxProbeRunner runner, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new(false, ["host is not Linux"]);
        }

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

        string? pid = null;
        string? limit = null;
        var interfaces = new List<string>();
        foreach (var line in output.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

        return new(reasons.Count == 0, reasons);
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
