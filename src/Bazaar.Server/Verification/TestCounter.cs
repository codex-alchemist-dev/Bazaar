using System.Text.RegularExpressions;

namespace Bazaar.Server.Verification;

/// <summary>
/// Static test count for xUnit sources: one per <c>[Fact]</c>, and per <c>[Theory]</c> one per
/// <c>[InlineData]</c> row that follows it (at least one). Deterministic and needs no build, so base and head compare cleanly.
/// </summary>
public static partial class TestCounter
{
    public static int Count(string source)
    {
        var markers = TestAttribute().Matches(source);
        var total = 0;
        for (var i = 0; i < markers.Count; i++)
        {
            if (markers[i].Groups["kind"].Value == "Fact")
            {
                total++;
                continue;
            }

            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : source.Length;
            total += Math.Max(1, InlineRow().Count(source[start..end]));
        }

        return total;
    }

    [GeneratedRegex(@"^\s*\[\s*(?<kind>Fact|Theory)\b", RegexOptions.Multiline)]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"\[\s*InlineData\b")]
    private static partial Regex InlineRow();
}
