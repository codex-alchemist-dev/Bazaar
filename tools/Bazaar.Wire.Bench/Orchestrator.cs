using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bazaar.Wire.Bench;

/// <summary>Runs one child process per codec and run, so JIT state and heap never carry over, and prints the result tables.</summary>
internal static class Orchestrator
{
    private sealed record Row(string Codec, int Run, string Corpus, string Metric, double Value);

    public static int Run(int runs, string? rawPath, string? mdPath)
    {
        var rows = new List<Row>();
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var dll = Path.GetFileName(exe).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ? typeof(Orchestrator).Assembly.Location : null;
        for (var run = 1; run <= runs; run++)
        {
            foreach (var codec in CodecRunner.CodecNames)
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                if (dll is not null) psi.ArgumentList.Add(dll);
                psi.ArgumentList.Add("--codec");
                psi.ArgumentList.Add(codec);
                psi.ArgumentList.Add("--run");
                psi.ArgumentList.Add(run.ToString(CultureInfo.InvariantCulture));
                Console.Error.WriteLine($"run {run} {codec} ...");
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException($"{codec} run {run} failed: {stderr.Result}{stdout}");
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.TrimEnd('\r').Split('\t');
                    if (parts.Length == 6 && parts[0] == "RESULT")
                    {
                        rows.Add(new Row(parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture), parts[3], parts[4], double.Parse(parts[5], CultureInfo.InvariantCulture)));
                    }
                }
            }
        }

        if (rawPath is not null)
        {
            File.WriteAllLines(rawPath, rows.Select(r => string.Join('\t', r.Codec, r.Run.ToString(CultureInfo.InvariantCulture), r.Corpus, r.Metric, r.Value.ToString("R", CultureInfo.InvariantCulture))).Prepend("codec\trun\tcorpus\tmetric\tvalue"));
        }

        var md = Tables(rows);
        if (mdPath is not null) File.WriteAllText(mdPath, md);
        Console.Write(md);
        return 0;
    }

    private static double[] Values(List<Row> rows, string codec, string corpus, string metric) =>
        [.. rows.Where(r => r.Codec == codec && r.Corpus == corpus && r.Metric == metric).OrderBy(r => r.Run).Select(r => r.Value)];

    private static double Median(double[] values)
    {
        var s = values.Order().ToArray();
        return s.Length == 0 ? double.NaN : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private static string N0(double v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static string N1(double v) => v.ToString(v < 10 ? "N2" : "N1", CultureInfo.InvariantCulture);

    private static string Series(double[] v, Func<double, string> f) => v.Length == 0 ? "n/a" : string.Join(" / ", v.Select(f));

    private static string Ratio(double value, double baseline) => double.IsNaN(value) || baseline == 0 ? "n/a" : (value / baseline).ToString("0.00", CultureInfo.InvariantCulture) + "x";

    private static string Tables(List<Row> rows)
    {
        var sb = new StringBuilder();
        foreach (var (corpus, title) in new[] { ("page200", "S6 200-message page (one gc.page frame)"), ("replay10k", "S6 10,000-frame replay (gc.message frames; per-frame figures)"), ("snapshot", "Synthetic Constellation snapshot (5,000 nodes, 20,000 edges, one frame)") })
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"#### {title}");
            sb.AppendLine();
            sb.AppendLine("Bytes (identical in every run; compression measured in run 1). Ratio is against JSON.");
            sb.AppendLine();
            sb.AppendLine("| Codec | Raw bytes | vs JSON | gzip | brotli |");
            sb.AppendLine("|---|---|---|---|---|");
            var jsonRaw = Values(rows, "json", corpus, "bytes_raw")[0];
            foreach (var c in CodecRunner.CodecNames)
            {
                var raw = Values(rows, c, corpus, "bytes_raw");
                var distinct = raw.Distinct().Count() > 1 ? " (VARIES)" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {c} | {N0(raw[0])}{distinct} | {Ratio(raw[0], jsonRaw)} | {N0(Values(rows, c, corpus, "bytes_gzip")[0])} | {N0(Values(rows, c, corpus, "bytes_brotli")[0])} |");
            }

            sb.AppendLine();
            sb.AppendLine("Time and allocations per frame (microseconds; bytes). Each cell: the three process runs, then the median of those. Each run's figure is the median of seven timed rounds.");
            sb.AppendLine();
            sb.AppendLine("| Codec | Encode us, runs 1 / 2 / 3 | Encode median | vs JSON | Decode us, runs 1 / 2 / 3 | Decode median | vs JSON | Encode alloc B | Decode alloc B |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            var jsonEnc = Median(Values(rows, "json", corpus, "encode_us_per_frame_median"));
            var jsonDec = Median(Values(rows, "json", corpus, "decode_us_per_frame_median"));
            foreach (var c in CodecRunner.CodecNames)
            {
                var enc = Values(rows, c, corpus, "encode_us_per_frame_median");
                var dec = Values(rows, c, corpus, "decode_us_per_frame_median");
                var encAlloc = Median(Values(rows, c, corpus, "encode_alloc_bytes_per_frame"));
                var decAlloc = Median(Values(rows, c, corpus, "decode_alloc_bytes_per_frame"));
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {c} | {Series(enc, N1)} | {N1(Median(enc))} | {Ratio(Median(enc), jsonEnc)} | {Series(dec, N1)} | {N1(Median(dec))} | {Ratio(Median(dec), jsonDec)} | {N0(encAlloc)} | {N0(decAlloc)} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
