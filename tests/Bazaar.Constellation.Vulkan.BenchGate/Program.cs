using System.Globalization;
using Bazaar.Constellation.Vulkan.BenchGate;

// Usage: BenchGate <baseline.csv[,baseline2.csv,...]> <candidate.csv> [maxRatio] [absoluteFloorMs]
// Several comma-separated baselines are merged into a slowest-seen envelope per row.
// Exit codes: 0 passed, 1 regression or missing row, 2 usage or input error.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: BenchGate <baseline.csv> <candidate.csv> [maxRatio] [absoluteFloorMs]");
    return 2;
}

try
{
    var defaults = new GateOptions();
    double ratio = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : defaults.MaxRatio;
    double floor = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : defaults.AbsoluteFloorMs;
    IReadOnlyList<BenchRow> baseline = BenchGate.Envelope(args[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => BenchGate.ParseCsv(File.ReadAllText(f))));
    IReadOnlyList<BenchRow> candidate = BenchGate.ParseCsv(File.ReadAllText(args[1]));
    if (baseline.Count == 0 || candidate.Count == 0)
    {
        Console.Error.WriteLine("baseline or candidate has no timed rows");
        return 2;
    }

    GateReport report = BenchGate.Compare(baseline, candidate, new GateOptions(ratio, floor));
    Console.Write(report.ToText());
    return report.Passed ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
