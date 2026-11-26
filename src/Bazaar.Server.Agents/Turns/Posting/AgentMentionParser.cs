using Bazaar.Server.Gc.Mentions;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>The result of scanning one agent post for mentions.</summary>
/// <param name="Spans">One span per recognised "@handle", in text order. Offsets are UTF-16 code units and cover the "@".</param>
/// <param name="Broadcasts">The broadcast handles found, "here" or "all", in text order, without the "@". Never spans.</param>
public sealed record AgentMentionScan(IReadOnlyList<MentionSpan> Spans, IReadOnlyList<string> Broadcasts);

/// <summary>
/// Finds "@handle" mentions in an agent's text (pure, no I/O). The spans it returns still go through
/// <see cref="MentionValidator"/> before anything is written.
/// </summary>
/// <remarks>
/// Rules: a mention starts at "@" that is at the start of the text or follows whitespace or opening punctuation, so "a@b" is not
/// one. The handle is the longest candidate that matches exactly (ordinal, case-insensitive) and is followed by the end of the
/// text, whitespace or punctuation. Text inside a Markdown code span or fenced code block is not scanned. "@here" and "@all" are
/// broadcasts: they are reported in <see cref="AgentMentionScan.Broadcasts"/> and never become spans, and they win over a member
/// with the same handle. Unknown handles produce nothing.
/// </remarks>
public static class AgentMentionParser
{
    private const string _openingPunctuation = "([{<\"'“‘«";
    private static readonly string[] _broadcastHandles = ["here", "all"];

    /// <summary>Scans <paramref name="text"/> for mentions of <paramref name="members"/> and for broadcasts.</summary>
    /// <param name="text">The agent's post text.</param>
    /// <param name="members">The candidate members. Only their handles are matched.</param>
    public static AgentMentionScan Scan(string text, IEnumerable<GcMentionMember> members)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(members);

        var candidates = members.Where(m => !string.IsNullOrEmpty(m.Handle)).ToList();
        var inCode = CodeMask(text);
        var spans = new List<MentionSpan>();
        var broadcasts = new List<string>();

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '@' || inCode[i] || (i > 0 && !StartsMention(text[i - 1])))
            {
                i++;
                continue;
            }

            var handleStart = i + 1;
            if (BroadcastAt(text, handleStart) is { } broadcast)
            {
                broadcasts.Add(broadcast);
                i = handleStart + broadcast.Length;
                continue;
            }

            var match = LongestMemberAt(text, handleStart, candidates);
            if (match is null)
            {
                i++;
                continue;
            }

            spans.Add(new MentionSpan(i, 1 + match.Handle.Length, match.PrincipalId));
            i = handleStart + match.Handle.Length;
        }

        return new AgentMentionScan(spans, broadcasts);
    }

    private static bool StartsMention(char previous) =>
        char.IsWhiteSpace(previous) || _openingPunctuation.Contains(previous);

    private static bool EndsHandle(string text, int end) =>
        end == text.Length || char.IsWhiteSpace(text[end]) || char.IsPunctuation(text[end]);

    private static string? BroadcastAt(string text, int handleStart)
    {
        foreach (var name in _broadcastHandles)
        {
            if (Matches(text, handleStart, name) && EndsHandle(text, handleStart + name.Length))
                return name;
        }

        return null;
    }

    private static GcMentionMember? LongestMemberAt(string text, int handleStart, List<GcMentionMember> candidates)
    {
        GcMentionMember? best = null;
        foreach (var candidate in candidates)
        {
            if (!Matches(text, handleStart, candidate.Handle) || !EndsHandle(text, handleStart + candidate.Handle.Length))
                continue;
            if (best is null || candidate.Handle.Length > best.Handle.Length)
                best = candidate;
        }

        return best;
    }

    private static bool Matches(string text, int start, string handle) =>
        start + handle.Length <= text.Length
        && string.Compare(text, start, handle, 0, handle.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Marks every UTF-16 unit that sits inside a fenced code block or a code span.</summary>
    private static bool[] CodeMask(string text)
    {
        var mask = new bool[text.Length];
        MarkFencedBlocks(text, mask);
        MarkCodeSpans(text, mask);
        return mask;
    }

    private static void MarkFencedBlocks(string text, bool[] mask)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var lineEnd = LineEnd(text, pos);
            var (fenceChar, runLength) = FenceAt(text, pos, lineEnd);
            if (runLength < 3)
            {
                pos = lineEnd + 1;
                continue;
            }

            // Opening fence found. The block runs to a closing line of the same character at least as long, or to the end.
            var blockEnd = text.Length;
            var scan = lineEnd + 1;
            while (scan < text.Length)
            {
                var end = LineEnd(text, scan);
                var (closeChar, closeRun) = FenceAt(text, scan, end);
                if (closeChar == fenceChar && closeRun >= runLength && IsOnlyFence(text, scan, end, closeChar))
                {
                    blockEnd = end;
                    break;
                }

                scan = end + 1;
            }

            Array.Fill(mask, true, pos, blockEnd - pos);
            pos = blockEnd + 1;
        }
    }

    private static int LineEnd(string text, int from)
    {
        var end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end;
    }

    /// <summary>The fence character and run length of the line text[from..lineEnd), after up to any leading spaces or tabs.</summary>
    private static (char Char, int Run) FenceAt(string text, int from, int lineEnd)
    {
        var i = from;
        while (i < lineEnd && text[i] is ' ' or '\t')
            i++;
        if (i >= lineEnd || text[i] is not ('`' or '~'))
            return ('\0', 0);

        var c = text[i];
        var run = 0;
        while (i + run < lineEnd && text[i + run] == c)
            run++;
        return (c, run);
    }

    /// <summary>True when the line holds nothing but the fence after its leading spaces and tabs.</summary>
    private static bool IsOnlyFence(string text, int from, int lineEnd, char fenceChar)
    {
        var i = from;
        while (i < lineEnd && text[i] is ' ' or '\t')
            i++;
        while (i < lineEnd && text[i] == fenceChar)
            i++;
        while (i < lineEnd && text[i] is ' ' or '\t')
            i++;
        return i == lineEnd;
    }

    private static void MarkCodeSpans(string text, bool[] mask)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (mask[i] || text[i] != '`')
            {
                i++;
                continue;
            }

            var openLength = RunLengthAt(text, i);
            var close = FindClosingRun(text, mask, i + openLength, openLength);
            if (close < 0)
            {
                // No closing run: the backticks are literal text.
                i += openLength;
                continue;
            }

            Array.Fill(mask, true, i, close + openLength - i);
            i = close + openLength;
        }
    }

    private static int RunLengthAt(string text, int start)
    {
        var n = 0;
        while (start + n < text.Length && text[start + n] == '`')
            n++;
        return n;
    }

    /// <summary>The start of the next run of exactly <paramref name="length"/> unmasked backticks, or -1.</summary>
    private static int FindClosingRun(string text, bool[] mask, int from, int length)
    {
        var i = from;
        while (i < text.Length)
        {
            if (mask[i])
                return -1;
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            var run = RunLengthAt(text, i);
            if (run == length)
                return i;
            i += run;
        }

        return -1;
    }
}
