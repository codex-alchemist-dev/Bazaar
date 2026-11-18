namespace Bazaar.Client.Calcinato.Modal;

/// <summary>
/// Vim-style word motions over the rows of a screen. A row break counts as a blank, and an empty row is a stop.
/// Character classes: blank, punctuation, word (letters, digits and underscore).
/// </summary>
internal static class WordMotion
{
    private const int _blank = 0;
    private const int _punctuation = 1;
    private const int _word = 2;

    internal delegate (int Row, int Col) Step(Func<int, string> lines, int total, int row, int col);

    /// <summary>Start of the next word (<c>w</c>).</summary>
    public static (int Row, int Col) Forward(Func<int, string> lines, int total, int row, int col)
    {
        var start = (Row: row, Col: col);
        var p = start;
        int cls = ClassAt(lines, p);
        if (cls != _blank)
        {
            while (true)
            {
                var next = Next(lines, total, p);
                if (next is null)
                {
                    return LastCharacter(lines, total);
                }

                p = next.Value;
                if (p.Row != start.Row || ClassAt(lines, p) != cls)
                {
                    break;
                }
            }
        }

        while (ClassAt(lines, p) == _blank)
        {
            if (p.Row != start.Row && LengthOf(lines, p.Row) == 0)
            {
                return (p.Row, 0);
            }

            var next = Next(lines, total, p);
            if (next is null)
            {
                return LastCharacter(lines, total);
            }

            p = next.Value;
        }

        return p;
    }

    /// <summary>Start of the previous word (<c>b</c>).</summary>
    public static (int Row, int Col) Backward(Func<int, string> lines, int total, int row, int col)
    {
        var p = (Row: row, Col: col);
        var prev = Previous(lines, p);
        if (prev is null)
        {
            return (0, 0);
        }

        p = prev.Value;
        while (ClassAt(lines, p) == _blank)
        {
            if (LengthOf(lines, p.Row) == 0)
            {
                return p;
            }

            prev = Previous(lines, p);
            if (prev is null)
            {
                return (0, 0);
            }

            p = prev.Value;
        }

        int cls = ClassAt(lines, p);
        while (true)
        {
            var before = Previous(lines, p);
            if (before is null || before.Value.Row != p.Row || ClassAt(lines, before.Value) != cls)
            {
                return p;
            }

            p = before.Value;
        }
    }

    /// <summary>End of the current or next word (<c>e</c>).</summary>
    public static (int Row, int Col) End(Func<int, string> lines, int total, int row, int col)
    {
        var next = Next(lines, total, (Row: row, Col: col));
        if (next is null)
        {
            return (row, col);
        }

        var p = next.Value;
        while (ClassAt(lines, p) == _blank)
        {
            var step = Next(lines, total, p);
            if (step is null)
            {
                return p;
            }

            p = step.Value;
        }

        int cls = ClassAt(lines, p);
        while (true)
        {
            var after = Next(lines, total, p);
            if (after is null || after.Value.Row != p.Row || ClassAt(lines, after.Value) != cls)
            {
                return p;
            }

            p = after.Value;
        }
    }

    // A position with Col equal to the row length is the row break; it is blank.
    private static int ClassAt(Func<int, string> lines, (int Row, int Col) p)
    {
        string text = lines(p.Row);
        if (p.Col >= text.Length)
        {
            return _blank;
        }

        char c = text[p.Col];
        if (char.IsWhiteSpace(c))
        {
            return _blank;
        }

        return char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c) ? _word : _punctuation;
    }

    private static int LengthOf(Func<int, string> lines, int row) => lines(row).Length;

    private static (int Row, int Col)? Next(Func<int, string> lines, int total, (int Row, int Col) p)
    {
        int length = LengthOf(lines, p.Row);
        if (p.Col < length)
        {
            return (p.Row, p.Col + 1);
        }

        if (p.Row + 1 < total)
        {
            return (p.Row + 1, 0);
        }

        return null;
    }

    private static (int Row, int Col)? Previous(Func<int, string> lines, (int Row, int Col) p)
    {
        if (p.Col > 0)
        {
            return (p.Row, Math.Max(0, Math.Min(p.Col, LengthOf(lines, p.Row)) - 1));
        }

        if (p.Row > 0)
        {
            return (p.Row - 1, LengthOf(lines, p.Row - 1));
        }

        return null;
    }

    private static (int Row, int Col) LastCharacter(Func<int, string> lines, int total)
    {
        int row = total - 1;
        return (row, Math.Max(0, LengthOf(lines, row) - 1));
    }
}
