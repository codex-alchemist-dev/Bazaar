using System.Globalization;
using System.Text;

namespace Bazaar.Constellation.Vulkan.BenchGate;

/// <summary>One measured row of the Vulkan node benchmark CSV, reduced to the fields the gate compares.</summary>
public sealed record BenchRow(string Section, long TotalNodes, long InstancesDrawn, double GpuP50Ms, double GpuP95Ms)
{
    /// <summary>The identity of a row: the same scenario in a baseline and a candidate run.</summary>
    public string Key => $"{Section}|{TotalNodes}|{InstancesDrawn}";
}

/// <summary>How much slower than the baseline a row may be before the gate fails.</summary>
/// <param name="MaxRatio">Candidate / baseline above this ratio is a regression (for example 1.25).</param>
/// <param name="AbsoluteFloorMs">Differences smaller than this many milliseconds never fail; sub-millisecond timer noise is not a regression.</param>
public sealed record GateOptions(double MaxRatio = 1.25, double AbsoluteFloorMs = 0.25);

public enum GateVerdict
{
    Ok,
    Regressed,
    Missing,
    New,
}

public sealed record GateRowResult(string Key, string Metric, GateVerdict Verdict, double? Baseline, double? Candidate);

public sealed record GateReport(IReadOnlyList<GateRowResult> Rows)
{
    /// <summary>The gate fails on a regression or a baseline row the candidate no longer measures.</summary>
    public bool Passed => Rows.All(r => r.Verdict is GateVerdict.Ok or GateVerdict.New);

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (GateRowResult r in Rows)
        {
            string b = r.Baseline?.ToString("F3", CultureInfo.InvariantCulture) ?? "-";
            string c = r.Candidate?.ToString("F3", CultureInfo.InvariantCulture) ?? "-";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Verdict,-9} {r.Key} {r.Metric} baseline={b} candidate={c}");
        }

        sb.AppendLine(Passed ? "GATE PASSED" : "GATE FAILED");
        return sb.ToString();
    }
}

public static class BenchGate
{
    private const string _gpuP50 = "gpu_p50_ms";
    private const string _gpuP95 = "gpu_p95_ms";

    /// <summary>Parses the CSV written by <c>Bazaar.Constellation.Vulkan.Bench</c>. Unknown columns are ignored; rows without GPU timings are skipped.</summary>
    public static IReadOnlyList<BenchRow> ParseCsv(string csv)
    {
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        string[] header = lines[0].Split(',');
        int section = Array.IndexOf(header, "section");
        int nodes = Array.IndexOf(header, "total_nodes");
        int instances = Array.IndexOf(header, "instances_drawn");
        int p50 = Array.IndexOf(header, _gpuP50);
        int p95 = Array.IndexOf(header, _gpuP95);
        if (section < 0 || nodes < 0 || instances < 0 || p50 < 0 || p95 < 0)
        {
            throw new FormatException("CSV header is missing one of: section, total_nodes, instances_drawn, gpu_p50_ms, gpu_p95_ms.");
        }

        var rows = new List<BenchRow>();
        foreach (string line in lines.Skip(1))
        {
            string[] f = line.Split(',');
            if (f.Length <= Math.Max(Math.Max(section, nodes), Math.Max(instances, Math.Max(p50, p95))))
            {
                continue;
            }

            if (!double.TryParse(f[p50], NumberStyles.Float, CultureInfo.InvariantCulture, out double v50)
                || !double.TryParse(f[p95], NumberStyles.Float, CultureInfo.InvariantCulture, out double v95)
                || !long.TryParse(f[nodes], NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
                || !long.TryParse(f[instances], NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
            {
                continue;
            }

            rows.Add(new BenchRow(f[section], n, i, v50, v95));
        }

        return rows;
    }

    /// <summary>Compares a candidate run against a baseline run, row by row, on GPU p50 and p95.</summary>
    public static GateReport Compare(IReadOnlyList<BenchRow> baseline, IReadOnlyList<BenchRow> candidate, GateOptions options)
    {
        Dictionary<string, BenchRow> cand = candidate.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last());
        var results = new List<GateRowResult>();
        var seen = new HashSet<string>();

        foreach (BenchRow b in baseline)
        {
            if (!seen.Add(b.Key))
            {
                continue;
            }

            if (!cand.TryGetValue(b.Key, out BenchRow? c))
            {
                results.Add(new GateRowResult(b.Key, "row", GateVerdict.Missing, null, null));
                continue;
            }

            results.Add(Judge(b.Key, _gpuP50, b.GpuP50Ms, c.GpuP50Ms, options));
            results.Add(Judge(b.Key, _gpuP95, b.GpuP95Ms, c.GpuP95Ms, options));
        }

        foreach (BenchRow c in cand.Values.Where(r => !seen.Contains(r.Key)))
        {
            results.Add(new GateRowResult(c.Key, "row", GateVerdict.New, null, null));
        }

        return new GateReport(results);
    }

    private static GateRowResult Judge(string key, string metric, double baseline, double candidate, GateOptions options)
    {
        bool slower = candidate - baseline > options.AbsoluteFloorMs && candidate > baseline * options.MaxRatio;
        return new GateRowResult(key, metric, slower ? GateVerdict.Regressed : GateVerdict.Ok, baseline, candidate);
    }
}
