namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>How an aligned diff row differs between the two sides.</summary>
public enum DiffRowKind
{
    /// <summary>Both sides hold the same line.</summary>
    Equal,

    /// <summary>The line exists only on the original side (the modified side shows a filler).</summary>
    Removed,

    /// <summary>The line exists only on the modified side (the original side shows a filler).</summary>
    Added,

    /// <summary>A removed line and an added line share the row.</summary>
    Changed,
}

/// <summary>One aligned row of a side-by-side diff. A null side is a filler line.</summary>
public readonly record struct DiffRow(string? Left, string? Right, DiffRowKind Kind);

/// <summary>
/// A line diff (longest common subsequence) producing aligned rows for a side-by-side view (doc 57 §2). The repo has no
/// reusable text diff, so this small routine lives in the module. Common head and tail are trimmed first; when the
/// remaining middle is larger than <see cref="MaxCells"/> line pairs, it degrades to one replaced block instead of an exact diff.
/// </summary>
public static class LineDiff
{
    /// <summary>The largest middle section (original lines times modified lines) diffed exactly.</summary>
    public const long MaxCells = 4_000_000;

    /// <summary>Splits text into lines on LF or CRLF. A trailing line break does not add an empty line; empty text has no lines.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>(text.Split('\n'));
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        return lines;
    }

    /// <summary>Diffs the two texts into aligned rows.</summary>
    public static IReadOnlyList<DiffRow> Compute(string original, string modified)
    {
        IReadOnlyList<string> a = SplitLines(original);
        IReadOnlyList<string> b = SplitLines(modified);
        var rows = new List<DiffRow>(Math.Max(a.Count, b.Count));

        int head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head])
        {
            rows.Add(new DiffRow(a[head], b[head], DiffRowKind.Equal));
            head++;
        }

        int tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail])
        {
            tail++;
        }

        int n = a.Count - head - tail;
        int m = b.Count - head - tail;
        var removed = new List<string>();
        var added = new List<string>();

        void Flush()
        {
            int paired = Math.Min(removed.Count, added.Count);
            for (int i = 0; i < paired; i++)
            {
                rows.Add(new DiffRow(removed[i], added[i], DiffRowKind.Changed));
            }

            for (int i = paired; i < removed.Count; i++)
            {
                rows.Add(new DiffRow(removed[i], null, DiffRowKind.Removed));
            }

            for (int i = paired; i < added.Count; i++)
            {
                rows.Add(new DiffRow(null, added[i], DiffRowKind.Added));
            }

            removed.Clear();
            added.Clear();
        }

        if ((long)n * m > MaxCells)
        {
            for (int i = 0; i < n; i++)
            {
                removed.Add(a[head + i]);
            }

            for (int j = 0; j < m; j++)
            {
                added.Add(b[head + j]);
            }

            Flush();
        }
        else
        {
            // lcs[i, j] is the common length of a[head + i ..] and b[head + j ..].
            int[,] lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[head + i] == b[head + j]
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0;
            int y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[head + x] == b[head + y])
                {
                    Flush();
                    rows.Add(new DiffRow(a[head + x], b[head + y], DiffRowKind.Equal));
                    x++;
                    y++;
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    removed.Add(a[head + x]);
                    x++;
                }
                else
                {
                    added.Add(b[head + y]);
                    y++;
                }
            }

            Flush();
        }

        for (int i = a.Count - tail; i < a.Count; i++)
        {
            rows.Add(new DiffRow(a[i], b[b.Count - (a.Count - i)], DiffRowKind.Equal));
        }

        return rows;
    }
}
