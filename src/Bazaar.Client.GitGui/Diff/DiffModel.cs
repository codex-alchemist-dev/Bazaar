namespace Bazaar.Client.GitGui.Diff;

/// <summary>What one diff row is.</summary>
public enum DiffLineKind
{
    /// <summary>The <c>@@ -a,b +c,d @@</c> line that opens a hunk.</summary>
    HunkHeader,
    Context,
    Added,
    Removed,

    /// <summary>The <c>\ No newline at end of file</c> marker.</summary>
    NoNewline,

    /// <summary>A run of unchanged lines hidden by the context-lines setting. <see cref="DiffLine.Text"/> says how many.</summary>
    Collapsed,
}

/// <summary>One row of a diff. Line numbers are null where the row has no number on that side.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

public sealed record DiffHunk(string Header, int OldStart, int NewStart, IReadOnlyList<DiffLine> Lines);

/// <summary>Why a file has no hunks to show.</summary>
public enum DiffNotice
{
    None,

    /// <summary>The file is binary.</summary>
    Binary,

    /// <summary>The server sent no patch (a pure rename or mode change, or a file the server left out).</summary>
    NoPatch,
}

/// <summary>A file's diff, parsed from the unified patch the server sent (doc 52). Pure data; the surface turns it into controls.</summary>
public sealed record DiffModel(
    string Path,
    string? OldPath,
    string Status,
    bool Truncated,
    DiffNotice Notice,
    IReadOnlyList<DiffHunk> Hunks,
    int Additions,
    int Deletions)
{
    /// <summary>The rows in display order: each hunk's header, then its lines.</summary>
    public IReadOnlyList<DiffLine> Rows()
    {
        var rows = new List<DiffLine>();
        foreach (var hunk in Hunks)
        {
            rows.Add(new DiffLine(DiffLineKind.HunkHeader, null, null, hunk.Header));
            rows.AddRange(hunk.Lines);
        }

        return rows;
    }

    /// <summary>
    /// A copy that keeps only <paramref name="contextLines"/> unchanged lines on each side of a change; longer runs become one
    /// <see cref="DiffLineKind.Collapsed"/> row. A hunk with no change keeps its lines as they are.
    /// </summary>
    public DiffModel WithContext(int contextLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);

        return this with { Hunks = Hunks.Select(hunk => hunk with { Lines = Collapse(hunk.Lines, contextLines) }).ToList() };
    }

    private static List<DiffLine> Collapse(IReadOnlyList<DiffLine> lines, int context)
    {
        var keep = new bool[lines.Count];
        var anyChange = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind is DiffLineKind.Added or DiffLineKind.Removed)
            {
                anyChange = true;
                for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                {
                    keep[j] = true;
                }
            }
            else if (lines[i].Kind != DiffLineKind.Context)
            {
                keep[i] = true;
            }
        }

        if (!anyChange)
        {
            return [.. lines];
        }

        var result = new List<DiffLine>();
        var hidden = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                FlushHidden(result, ref hidden);
                result.Add(lines[i]);
            }
            else
            {
                hidden++;
            }
        }

        FlushHidden(result, ref hidden);
        return result;
    }

    private static void FlushHidden(List<DiffLine> result, ref int hidden)
    {
        if (hidden > 0)
        {
            result.Add(new DiffLine(DiffLineKind.Collapsed, null, null, $"{hidden} unchanged line{(hidden == 1 ? string.Empty : "s")}"));
            hidden = 0;
        }
    }
}
