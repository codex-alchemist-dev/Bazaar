using System.Text;
using Bazaar.Contracts.Coding;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Maps one headless Luau check run to a <c>check_runs</c> row (docs/design/17-coding-agent-harness.md, Verification check 6).
/// Pure: no I/O, no clock, no randomness. The caller supplies the row id, the commit and the time.
/// A run passes only when every requested tool ran, reported no Error-severity diagnostic and its output was complete.
/// Anything else fails closed and records why. Tool output is untrusted text; it is only ever copied in bounded, sanitised form.
/// </summary>
public static class LuauCheckRunMapper
{
    /// <summary>Most diagnostic lines kept in a summary. The rest are counted in one closing line.</summary>
    public const int MaxDiagnosticLines = 20;

    /// <summary>Longest summary or reason line, in characters. Longer text is cut and ends with an ellipsis.</summary>
    public const int MaxLineCharacters = 200;

    private static readonly LuauToolKind[] _allTools = [LuauToolKind.Analyze, LuauToolKind.Selene, LuauToolKind.StyLuaCheck];

    /// <summary>
    /// Maps <paramref name="result"/> to a <c>check_runs</c> row. <paramref name="requestedTools"/> is the list the run asked for;
    /// an empty list means every tool, as <see cref="LuauChecker"/> reads it. A requested tool with no report fails the run.
    /// </summary>
    /// <param name="result">The checker's result for the run.</param>
    /// <param name="requestedTools">The tools the run asked for. Empty means all of them.</param>
    /// <param name="id">The row id. The caller supplies it so the mapping stays deterministic.</param>
    /// <param name="commit">The commit that was checked.</param>
    /// <param name="baseCommit">The base commit, when the run has one.</param>
    /// <param name="ranAt">When the run happened.</param>
    public static LuauCheckRunMapping ToCheckRun(
        LuauCheckResult result,
        IReadOnlyList<LuauToolKind> requestedTools,
        Guid id,
        string commit,
        string? baseCommit,
        DateTimeOffset ranAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(requestedTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        LuauToolKind[] tools = (requestedTools.Count == 0 ? _allTools : requestedTools.Distinct().ToArray())
            .OrderBy(tool => (int)tool)
            .ToArray();

        var reasons = new List<string>();
        var summary = new List<string>();
        foreach (LuauToolKind tool in tools)
        {
            LuauToolReport? report = result.Tools.FirstOrDefault(r => r.Tool == tool);
            int errors = CountDiagnostics(result, tool, LuauSeverity.Error);
            int warnings = CountDiagnostics(result, tool, LuauSeverity.Warning);

            string? reason = FailureReason(report, errors);
            if (reason is not null)
            {
                reasons.Add(Bound($"{ToolName(tool)}: {reason}"));
            }

            summary.Add(Bound(ToolLine(tool, report, errors, warnings)));
        }

        summary.AddRange(DiagnosticLines(result, tools));

        var record = new CheckRunRecord(
            id,
            commit,
            baseCommit,
            BuildExitCode: null,
            TestsPassed: null,
            TestsFailed: null,
            TestsSkipped: null,
            TestCountBase: null,
            Flags: [],
            ranAt);

        return new LuauCheckRunMapping(record, reasons.Count == 0, reasons, summary);
    }

    /// <summary>Why one tool fails the run, or null when it does not. A missing report fails; so does an Error diagnostic.</summary>
    private static string? FailureReason(LuauToolReport? report, int errors) => report switch
    {
        null => "did not run",
        { Status: LuauToolStatus.Unavailable } r => "unavailable" + Detail(r.Reason),
        { Status: LuauToolStatus.TimedOut } r => "timed out" + Detail(r.Reason),
        { Status: LuauToolStatus.Error } r => "failed" + Detail(r.Reason),
        _ when errors > 0 => $"{errors} error diagnostic(s)",

        // Cut output may hide an Error that was not parsed, so a cut report with findings cannot be a pass.
        { Status: LuauToolStatus.Findings, OutputTruncated: true } => "output truncated, so diagnostics may be incomplete",
        _ => null,
    };

    private static string Detail(string? reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason;

    private static string ToolLine(LuauToolKind tool, LuauToolReport? report, int errors, int warnings)
    {
        if (report is null)
        {
            return $"{ToolName(tool)}: not run";
        }

        string exit = report.ExitCode is int code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
        string cut = report.OutputTruncated ? ", output truncated" : string.Empty;
        return $"{ToolName(tool)}: {report.Status}, exit {exit}, {errors} error(s), {warnings} warning(s){cut}";
    }

    /// <summary>Diagnostics of the requested tools in a fixed order, capped at <see cref="MaxDiagnosticLines"/> plus one count line.</summary>
    private static IEnumerable<string> DiagnosticLines(LuauCheckResult result, LuauToolKind[] tools)
    {
        LuauDiagnostic[] ordered = result.Diagnostics
            .Where(d => tools.Contains(d.Tool))
            .OrderBy(d => (int)d.Tool)
            .ThenBy(d => (int)d.Severity)
            .ThenBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Column)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToArray();

        foreach (LuauDiagnostic diagnostic in ordered.Take(MaxDiagnosticLines))
        {
            yield return Bound(DiagnosticLine(diagnostic));
        }

        if (ordered.Length > MaxDiagnosticLines)
        {
            yield return $"... {ordered.Length - MaxDiagnosticLines} more diagnostic(s) not shown";
        }
    }

    private static string DiagnosticLine(LuauDiagnostic d)
    {
        var builder = new StringBuilder(ToolName(d.Tool)).Append(' ');
        builder.Append(string.IsNullOrEmpty(d.File) ? "(no file)" : d.File);
        if (d.Line > 0)
        {
            builder.Append(':').Append(d.Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (d.Column > 0)
            {
                builder.Append(':').Append(d.Column.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        builder.Append(' ').Append(d.Severity);
        if (!string.IsNullOrEmpty(d.Code))
        {
            builder.Append(' ').Append(d.Code);
        }

        return builder.Append(": ").Append(d.Message).ToString();
    }

    private static int CountDiagnostics(LuauCheckResult result, LuauToolKind tool, LuauSeverity severity) =>
        result.Diagnostics.Count(d => d.Tool == tool && d.Severity == severity);

    /// <summary>
    /// Bounds one line of untrusted text: cuts it before sanitising, replaces control characters with spaces, trims,
    /// and marks any cut with an ellipsis. The result is never longer than <see cref="MaxLineCharacters"/>.
    /// </summary>
    internal static string Bound(string text)
    {
        const int Ellipsis = 3;
        int keep = MaxLineCharacters - Ellipsis;
        bool cut = text.Length > keep;
        ReadOnlySpan<char> head = cut ? text.AsSpan(0, keep) : text.AsSpan();

        var chars = new char[head.Length];
        for (int i = 0; i < head.Length; i++)
        {
            chars[i] = char.IsControl(head[i]) ? ' ' : head[i];
        }

        string flat = new string(chars).Trim();
        return cut ? flat + "..." : flat;
    }

    private static string ToolName(LuauToolKind tool) => tool switch
    {
        LuauToolKind.Analyze => "luau-analyze",
        LuauToolKind.Selene => "selene",
        _ => "stylua --check",
    };
}
