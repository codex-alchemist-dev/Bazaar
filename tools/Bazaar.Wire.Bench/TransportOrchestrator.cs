using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bazaar.Wire.Bench;

/// <summary>Runs one child process per transport and run (fresh JIT and heap each time), collects the rows and prints the tables.</summary>
internal static class TransportOrchestrator
{
    public static int Run(int runs, TransportRunOptions options, IReadOnlyList<string> transports, string? rawPath, string? mdPath)
    {
        var rows = new List<TransportRow>();
        var info = new Dictionary<(string Transport, string Key), string>();
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var dll = Path.GetFileName(exe).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ? typeof(TransportOrchestrator).Assembly.Location : null;
        for (var run = 1; run <= runs; run++)
        {
            foreach (var transport in transports)
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                if (dll is not null) psi.ArgumentList.Add(dll);
                foreach (var a in new[]
                {
                    "--transport", transport, "--run", run.ToString(CultureInfo.InvariantCulture), "--condition", options.Condition,
                    "--duration", options.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture), "--rate", options.Rate.ToString(CultureInfo.InvariantCulture),
                })
                {
                    psi.ArgumentList.Add(a);
                }

                Console.Error.WriteLine($"run {run} {transport} ({options.Condition}) ...");
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var limit = options.Duration + (options.StepTimeout * (options.WarmConnects + options.Pages + (options.Reconnects * 2) + 4));
                if (!process.WaitForExit(limit))
                {
                    process.Kill(entireProcessTree: true);
                    rows.Add(new TransportRow(transport, options.Condition, "connect_cold_ms", run, double.NaN, "child process killed after " + limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s"));
                    continue;
                }

                process.WaitForExit();
                Parse(stdout.Result, rows, info);
                if (process.ExitCode != 0)
                {
                    rows.Add(new TransportRow(transport, options.Condition, "connect_cold_ms", run, double.NaN, "child exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + stderr.Result.ReplaceLineEndings(" ")));
                }
            }
        }

        var md = Tables(rows, info, transports, options, runs);
        if (rawPath is not null) File.WriteAllText(rawPath, TransportStats.Tsv(rows));
        if (mdPath is not null) File.WriteAllText(mdPath, md);
        Console.Write(md);
        return 0;
    }

    /// <summary>Reads child output lines. A FAIL for a metric that also has a RESULT in the same run is dropped (a later step failed, not this one).</summary>
    internal static void Parse(string output, List<TransportRow> rows, Dictionary<(string Transport, string Key), string> info)
    {
        var fails = new List<TransportRow>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length >= 6 && p[0] == "RESULT")
            {
                rows.Add(new TransportRow(p[1], p[3], p[4], int.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[5], CultureInfo.InvariantCulture)));
            }
            else if (p.Length >= 6 && p[0] == "FAIL")
            {
                fails.Add(new TransportRow(p[1], p[3], p[4], int.Parse(p[2], CultureInfo.InvariantCulture), double.NaN, p[5]));
            }
            else if (p.Length >= 6 && p[0] == "INFO")
            {
                info[(p[1], p[4])] = p[5];
            }
        }

        foreach (var f in fails)
        {
            if (!rows.Any(r => r.Transport == f.Transport && r.Condition == f.Condition && r.Metric == f.Metric && r.Run == f.Run && !double.IsNaN(r.Value))) rows.Add(f);
        }
    }

    private static string Tables(List<TransportRow> rows, Dictionary<(string Transport, string Key), string> info, IReadOnlyList<string> transports, TransportRunOptions options, int runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Environment (as printed by the harness)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {CpuModel()}; {Environment.ProcessorCount} logical CPUs");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Runtime: {RuntimeInformation.FrameworkDescription}; SDK: {SdkVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Condition: `{options.Condition}`; throughput window {options.Duration.TotalSeconds:0} s at {options.Rate} msg/s; {options.WarmConnects} warm connects, {options.Pages} warm pages, {options.Reconnects} forced drops per process run; {runs} process runs per transport");
        sb.AppendLine("- Loopback by IP (127.0.0.1), client and server in one process, JSON codec, TLS on every row, frames are S6 `gc.message` frames.");
        sb.AppendLine();
        sb.AppendLine("Negotiated protocol (seen by the server) and drop method per transport:");
        sb.AppendLine();
        sb.AppendLine("| Transport | Negotiated | Drop method used for the reconnect rows |");
        sb.AppendLine("|---|---|---|");
        foreach (var t in transports)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {t} | {info.GetValueOrDefault((t, "protocol"), "not connected")} | {info.GetValueOrDefault((t, "drop_method"), "n/a")} |");
        }

        sb.AppendLine();
        sb.Append(TransportStats.Markdown(rows, transports, options.Condition, runs));
        sb.AppendLine("Metric definitions: `connect_cold_ms` is the first connect in a fresh process (JIT included) until the client module returns; `connect_warm_ms` is the median of further connects, each a new connection and handshake; `replay_first_page_ms` is the request on the cold connection until the 200th of 200 `gc.message` frames arrives; `replay_page_ms` is the median of further pages; `latency_*` is arrival minus scheduled send time over the throughput window; `cpu_ms_per_1000_msgs` is process CPU (`Process.TotalProcessorTime`, client and server together) per 1,000 delivered messages; `reconnect_detect_ms` is from the forced server-side drop until the client sees the close; `reconnect_total_ms` adds an immediate reconnect (no backoff) and the first frame on the new connection.");
        sb.AppendLine();
        sb.AppendLine("Not measured by this harness:");
        sb.AppendLine();
        sb.AppendLine("| Row | Reason |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| webtransport | The server module exists, but no .NET client for it does (the module's real-network test only proves Kestrel serves HTTP/3), so nothing can connect to it. |");
        sb.AppendLine("| grpc-web | Only the server half is on main; there is no grpc-web client connector to measure. |");
        sb.AppendLine("| connect, resumed | No module exposes session resumption or 0-RTT (the quic module sets none), so there is no resumed connect to time. |");
        sb.AppendLine("| Android Cronet HTTP/3 against grpc over HTTP/2, owner PC | needs a device run |");
        if (OperatingSystem.IsWindows())
        {
            sb.AppendLine("| Loss (1% and 5%) and 50 ms RTT | not measurable on hosted Windows (no loss injection) |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string CpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line is not null) return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            // Fall through to the generic answer.
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static string SdkVersion()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            var v = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit();
            return string.IsNullOrEmpty(v) ? "unknown" : v;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown (dotnet not on PATH)";
        }
    }
}
