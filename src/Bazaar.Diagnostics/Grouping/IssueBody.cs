using System.Text;
using Bazaar.Diagnostics.Fingerprinting;

namespace Bazaar.Diagnostics.Grouping;

/// <summary>The facts about a group that go into its GitHub issue (doc 43 section 8.5).</summary>
/// <param name="State">The group, for counts, versions, device classes and OS families.</param>
/// <param name="ErrorCode">The registered error code, or null for an unhandled exception.</param>
/// <param name="ErrorType">The exception type's full name.</param>
/// <param name="Frames">The stack, top frame first. Only the flagged frame and the top five are printed.</param>
/// <param name="FlaggedLine">The line of the flagged frame when symbols resolved, otherwise null.</param>
/// <param name="RedactedMessage">The device-redacted message. Only its first line is printed, shortened.</param>
/// <param name="FiledBy">The name of the company server that files the issue, for the bot label (doc 21).</param>
public sealed record IssueContent(
    GroupState State,
    string? ErrorCode,
    string ErrorType,
    IReadOnlyList<FingerprintFrame> Frames,
    int? FlaggedLine,
    string? RedactedMessage,
    string FiledBy);

/// <summary>The title and body of a crash issue.</summary>
/// <param name="Title">The issue title.</param>
/// <param name="Body">The issue body in Markdown.</param>
public sealed record IssueText(string Title, string Body);

/// <summary>Renders the issue a crash group files (doc 43 section 8.5). No logs, screenshots or identifiers are ever printed.</summary>
public static class IssueBody
{
    /// <summary>The longest message line printed.</summary>
    public const int MaxMessageLength = 200;

    /// <summary>The most frames printed.</summary>
    public const int MaxFrames = 5;

    /// <summary>The longest title.</summary>
    public const int MaxTitleLength = 120;

    private const string _markerPrefix = "<!-- bazaar-crash-fp: ";
    private const string _markerSuffix = " -->";

    /// <summary>The hidden marker line that carries the fingerprint.</summary>
    public static string Marker(string fingerprint) => _markerPrefix + fingerprint + _markerSuffix;

    /// <summary>Renders the title and body.</summary>
    public static IssueText Render(IssueContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var flagged = Flagged(content.Frames);
        var title = content.ErrorCode is { } code
            ? $"{code} {content.ErrorType}"
            : flagged is null ? content.ErrorType : $"{content.ErrorType} in {flagged.Type}.{flagged.Method}";

        var state = content.State;
        var body = new StringBuilder();
        body.Append(Marker(state.Fingerprint)).Append("\n\n");
        body.Append("This issue was filed automatically by ").Append(Inline(content.FiledBy)).Append(". ")
            .Append("The content is automated and was redacted on the device.\n\n");
        body.Append("- Error code: ").Append(content.ErrorCode is { } c ? Inline(c) : "none").Append('\n');
        body.Append("- Error type: ").Append(Inline(content.ErrorType)).Append('\n');
        if (flagged is not null)
        {
            body.Append("- Flagged frame: ").Append(Inline($"{flagged.ModuleId} {flagged.Type}.{flagged.Method}"));
            if (content.FlaggedLine is { } line)
            {
                body.Append(" line ").Append(line);
            }

            body.Append('\n');
        }

        body.Append("- First seen in: ").Append(Inline(state.FirstSeenVersion)).Append('\n');
        body.Append("- Last seen in: ").Append(Inline(state.LastSeenVersion)).Append('\n');
        body.Append("- Occurrences: ").Append(state.OccurrenceCount).Append('\n');
        body.Append("- Device classes: ").Append(string.Join(", ", state.DeviceClasses.Select(Inline))).Append('\n');
        body.Append("- OS families: ").Append(string.Join(", ", state.OsFamilies.Select(Inline))).Append('\n');

        if (FirstLine(content.RedactedMessage) is { Length: > 0 } message)
        {
            body.Append("\nMessage: ").Append(Inline(message)).Append('\n');
        }

        var top = content.Frames.Take(MaxFrames).ToArray();
        if (top.Length > 0)
        {
            body.Append("\nTop frames:\n");
            foreach (var frame in top)
            {
                body.Append("1. ").Append(Inline($"{frame.ModuleId} {frame.Type}.{frame.Method}")).Append('\n');
            }
        }

        return new IssueText(Shorten(Plain(title), MaxTitleLength), body.ToString());
    }

    private static FingerprintFrame? Flagged(IReadOnlyList<FingerprintFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (frame.App)
            {
                return frame;
            }
        }

        return frames.Count > 0 ? frames[0] : null;
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var end = message.AsSpan().IndexOfAny('\r', '\n');
        return Shorten(end < 0 ? message : message[..end], MaxMessageLength);
    }

    // A code span with nothing that can end it, start a comment, or break the line.
    private static string Inline(string text) => "`" + Plain(text).Replace('`', '\'') + "`";

    private static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString().Replace("<!--", "< !--", StringComparison.Ordinal).Replace("-->", "-- >", StringComparison.Ordinal);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
