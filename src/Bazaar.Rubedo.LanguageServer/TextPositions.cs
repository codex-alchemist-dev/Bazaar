namespace Bazaar.Rubedo.LanguageServer;

/// <summary>
/// Maps character offsets (what the analysis returns) to LSP positions and back. Both count UTF-16 code units, so the mapping is
/// only about lines. A line ends at LF, CRLF or a lone CR.
/// </summary>
public static class TextPositions
{
    public static LspPosition ToPosition(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 0;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (IsLineBreak(text, i))
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new LspPosition(line, offset - lineStart);
    }

    public static LspRange ToRange(string text, int start, int end) => new(ToPosition(text, start), ToPosition(text, end));

    /// <summary>Offset of a position; a line past the end maps to the end of the text and a column past the line end to the line end.</summary>
    public static int ToOffset(string text, int line, int character)
    {
        int currentLine = 0;
        int i = 0;
        while (currentLine < line && i < text.Length)
        {
            if (IsLineBreak(text, i))
            {
                currentLine++;
            }

            i++;
        }

        if (currentLine < line)
        {
            return text.Length;
        }

        int lineEnd = i;
        while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r')
        {
            lineEnd++;
        }

        return Math.Min(i + Math.Max(character, 0), lineEnd);
    }

    // The character at i ends a line: LF, or CR that is not the first half of CRLF.
    private static bool IsLineBreak(string text, int i) =>
        text[i] == '\n' || (text[i] == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n'));
}
