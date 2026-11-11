// Required CodeQL Analyze check runs on this head.
namespace Bazaar.Constellation.Bench;

// Node counts for the layout and render benchmark. The standard run covers the 5k and 10k graphs that S14 measures.
// 100k and 1M are large and slow, so they run only with --large or when named explicitly.
public static class BenchNodeCounts
{
    public const string LargeFlag = "--large";

    public static IReadOnlyList<int> Standard { get; } = [5_000, 10_000];

    public static IReadOnlyList<int> Large { get; } = [100_000, 1_000_000];

    // Usage: [--large] [nodeCounts comma-separated]. An explicit list replaces the defaults.
    public static int[] Resolve(string[] args)
    {
        bool large = args.Contains(LargeFlag);
        string[] explicitCounts = args.Where(a => a != LargeFlag).ToArray();
        if (explicitCounts.Length > 0)
        {
            return explicitCounts[0].Split(',').Select(int.Parse).ToArray();
        }

        return large ? [.. Standard, .. Large] : [.. Standard];
    }
}
