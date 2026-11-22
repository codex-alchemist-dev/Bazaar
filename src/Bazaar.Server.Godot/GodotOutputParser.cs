using System.Text.RegularExpressions;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>
/// Parses Godot console output into diagnostics. Godot output is untrusted text. Assumed line shapes (from memory of the
/// engine's console format, not verified against a recorded 4.7.2 binary; re-record fixtures from the pinned binary):
/// <c>SCRIPT ERROR: message</c> or <c>ERROR: message</c> followed by an indented <c>at: where (file:line)</c> line, an
/// inline <c>res://file:line</c> in the message, and <c>WARNING: message</c>. ANSI colour codes are removed. At most
/// <see cref="MaxDiagnostics"/> diagnostics are kept and each message is cut at <see cref="MaxMessageLength"/> characters.
/// </summary>
internal static partial class GodotOutputParser
{
    /// <summary>Most diagnostics kept from one run.</summary>
    public const int MaxDiagnostics = 200;

    /// <summary>Longest message kept, in characters.</summary>
    public const int MaxMessageLength = 500;

    public static IReadOnlyList<GodotDiagnostic> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var diagnostics = new List<GodotDiagnostic>();
        Pending? pending = null;

        foreach (string raw in output.Split('\n'))
        {
            string line = AnsiCode().Replace(raw.TrimEnd('\r'), string.Empty);

            Match header = Header().Match(line);
            if (header.Success)
            {
                Flush(diagnostics, ref pending);
                pending = StartPending(header);
                continue;
            }

            if (pending is not null)
            {
                Match location = AtLine().Match(line);
                if (location.Success)
                {
                    // The first "at:" line wins; later ones are stack frames.
                    if (pending.Line == 0 && TryLocation(location.Groups["where"].Value, out string file, out int lineNumber))
                    {
                        pending.File = file;
                        pending.Line = lineNumber;
                    }

                    continue;
                }
            }
        }

        Flush(diagnostics, ref pending);
        return diagnostics;
    }

    private static Pending StartPending(Match header)
    {
        string kind = header.Groups["kind"].Value;
        string message = header.Groups["message"].Value.Trim();
        var pending = new Pending
        {
            Severity = kind.EndsWith("WARNING", StringComparison.Ordinal) ? GodotDiagnosticSeverity.Warning : GodotDiagnosticSeverity.Error,
            Source = kind.StartsWith("SCRIPT", StringComparison.Ordinal) ? GodotDiagnosticSource.Script : GodotDiagnosticSource.Engine,
            Message = message,
        };

        Match inline = InlineLocation().Match(message);
        if (inline.Success)
        {
            pending.File = StripRes(inline.Groups["file"].Value);
            pending.Line = int.Parse(inline.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return pending;
    }

    private static void Flush(List<GodotDiagnostic> diagnostics, ref Pending? pending)
    {
        if (pending is not null && diagnostics.Count < MaxDiagnostics)
        {
            string message = pending.Message.Length > MaxMessageLength ? pending.Message[..MaxMessageLength] : pending.Message;
            diagnostics.Add(new GodotDiagnostic(pending.Severity, pending.Source, pending.File, pending.Line, message));
        }

        pending = null;
    }

    private static bool TryLocation(string where, out string file, out int line)
    {
        file = string.Empty;
        line = 0;
        Match match = TrailingLocation().Match(where);
        if (!match.Success || !int.TryParse(match.Groups["line"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out line))
        {
            return false;
        }

        file = StripRes(match.Groups["file"].Value);
        return true;
    }

    private static string StripRes(string file) => file.StartsWith("res://", StringComparison.Ordinal) ? file["res://".Length..] : file;

    private sealed class Pending
    {
        public GodotDiagnosticSeverity Severity { get; init; }

        public GodotDiagnosticSource Source { get; init; }

        public string Message { get; init; } = string.Empty;

        public string File { get; set; } = string.Empty;

        public int Line { get; set; }
    }

    [GeneratedRegex("\u001b\\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiCode();

    [GeneratedRegex(@"^(?<kind>SCRIPT ERROR|SCRIPT WARNING|ERROR|WARNING):\s*(?<message>.*)$")]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s+at:\s*(?<where>.*)$")]
    private static partial Regex AtLine();

    [GeneratedRegex(@"\((?<file>[^()]+?):(?<line>\d+)\)\s*$")]
    private static partial Regex TrailingLocation();

    [GeneratedRegex(@"(?<file>res://[^\s:]+):(?<line>\d+)")]
    private static partial Regex InlineLocation();
}
