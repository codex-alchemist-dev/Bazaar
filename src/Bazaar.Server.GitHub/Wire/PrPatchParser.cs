using System.Globalization;
using System.Text.RegularExpressions;
using Bazaar.Contracts.GitHub;

namespace Bazaar.Server.GitHub.Wire;

/// <summary>Splits a GitHub unified-diff patch (<see cref="Adapter.PrFile.Patch"/>) into hunks.</summary>
internal static partial class PrPatchParser
{
    [GeneratedRegex(@"^@@ -(?<os>\d+)(?:,(?<oc>\d+))? \+(?<ns>\d+)(?:,(?<nc>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    public static IReadOnlyList<PrHunk> Parse(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return [];
        }

        var hunks = new List<PrHunk>();
        string? header = null;
        var os = 0;
        var oc = 0;
        var ns = 0;
        var nc = 0;
        var lines = new List<string>();

        foreach (var line in patch.Split('\n'))
        {
            var match = HunkHeader().Match(line);
            if (match.Success)
            {
                if (header is not null)
                {
                    hunks.Add(new PrHunk(header, os, oc, ns, nc, lines.ToArray()));
                    lines.Clear();
                }

                header = line;
                os = Num(match, "os");
                oc = Count(match, "oc");
                ns = Num(match, "ns");
                nc = Count(match, "nc");
            }
            else if (header is not null)
            {
                lines.Add(line.TrimEnd('\r'));
            }
        }

        if (header is not null)
        {
            hunks.Add(new PrHunk(header, os, oc, ns, nc, lines.ToArray()));
        }

        return hunks;
    }

    private static int Num(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static int Count(Match m, string group) => m.Groups[group].Success ? Num(m, group) : 1;
}
