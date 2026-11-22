using System.Text.RegularExpressions;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Parses <c>stylua --check</c> output. Assumed shape (not verified against a recorded binary, see the fixtures): one
/// header per file that differs, <c>Diff in &lt;file&gt;:</c> or <c>Diff in &lt;file&gt; at line &lt;n&gt;:</c>, followed by a
/// unified diff that is ignored. Each header becomes one Warning with code <c>format</c>. The line is 0 when the header
/// names none, meaning the whole file.
/// </summary>
public static partial class StyLuaOutputParser
{
    /// <summary>Parses the whole output. An empty or unrecognised output yields no diagnostics.</summary>
    public static IReadOnlyList<LuauDiagnostic> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var diagnostics = new List<LuauDiagnostic>();
        foreach (string raw in output.Split('\n'))
        {
            Match match = DiffHeader().Match(raw.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            int line = match.Groups["line"].Success
                ? int.Parse(match.Groups["line"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            diagnostics.Add(new LuauDiagnostic(
                LuauToolKind.StyLuaCheck,
                match.Groups["file"].Value.Trim(),
                line,
                0,
                LuauSeverity.Warning,
                "format",
                "File differs from StyLua's formatting."));
        }

        return diagnostics;
    }

    [GeneratedRegex(@"^Diff in (?<file>.+?)(?: at line (?<line>\d+))?:?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DiffHeader();
}
