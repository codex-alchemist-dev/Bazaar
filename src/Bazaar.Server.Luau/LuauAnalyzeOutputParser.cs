using System.Text.RegularExpressions;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Parses <c>luau-analyze</c> text output into diagnostics. Assumed line shape (not verified against a recorded binary,
/// see the fixtures): <c>file(line,col[-endcol]): Kind: message</c>. A GNU-style fallback accepts
/// <c>file:line:col: error|warning: message</c>. Lines that match neither shape (summaries, blank lines) are skipped.
/// Severity is Warning for lint kinds (<c>Lint*</c>) and kinds containing <c>Warning</c>; everything else is Error.
/// </summary>
public static partial class LuauAnalyzeOutputParser
{
    /// <summary>Parses the whole output. An empty or unrecognised output yields no diagnostics.</summary>
    public static IReadOnlyList<LuauDiagnostic> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var diagnostics = new List<LuauDiagnostic>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match match = PlainLine().Match(line);
            if (match.Success)
            {
                diagnostics.Add(FromMatch(match, match.Groups["kind"].Value, match.Groups["message"].Value));
                continue;
            }

            match = GnuLine().Match(line);
            if (match.Success)
            {
                string kind = match.Groups["severity"].Value;
                diagnostics.Add(FromMatch(match, kind, match.Groups["message"].Value, kind.Equals("warning", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return diagnostics;
    }

    private static LuauDiagnostic FromMatch(Match match, string kind, string message, bool forceWarning = false)
    {
        string code = kind.Trim();
        bool warning = forceWarning
            || code.StartsWith("Lint", StringComparison.Ordinal)
            || code.Contains("Warning", StringComparison.OrdinalIgnoreCase);
        return new LuauDiagnostic(
            LuauToolKind.Analyze,
            match.Groups["file"].Value,
            int.Parse(match.Groups["line"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["column"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture),
            warning ? LuauSeverity.Warning : LuauSeverity.Error,
            code.Equals("warning", StringComparison.OrdinalIgnoreCase) || code.Equals("error", StringComparison.OrdinalIgnoreCase) ? string.Empty : code,
            message.Trim());
    }

    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)(?:-\d+)?\):\s*(?<kind>[A-Za-z][A-Za-z0-9_ ]*?):\s*(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainLine();

    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<column>\d+):\s*(?<severity>error|warning):\s*(?<message>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GnuLine();
}
