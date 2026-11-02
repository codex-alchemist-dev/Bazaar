namespace Bazaar.Server.Vault.Markdown;

/// <summary>Finds the YAML front matter block at the start of a document. Owned by VLT1 (front-matter reader).</summary>
public static class FrontMatterSplitter
{
    private const char _byteOrderMark = '\uFEFF';
    private const string _fence = "---";

    private static readonly FrontMatterSplit _none = new(null, 0, 0);

    /// <summary>
    /// Splits the front matter from the body. Offsets are absolute to <paramref name="source"/>.
    /// An unterminated block is not an error; it is treated as no front matter.
    /// </summary>
    public static FrontMatterSplit Split(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int position = source.Length > 0 && source[0] == _byteOrderMark ? 1 : 0;

        // The opening fence must be a full line followed by a newline.
        int openingNewline = source.IndexOf('\n', position);
        if (openingNewline < 0 || !IsFenceLine(source, position, openingNewline))
        {
            return _none;
        }

        int yamlStart = openingNewline + 1;
        int lineStart = yamlStart;
        while (lineStart < source.Length)
        {
            int newline = source.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? source.Length : newline;
            if (IsFenceLine(source, lineStart, lineEnd))
            {
                int bodyStart = newline < 0 ? source.Length : newline + 1;
                return new FrontMatterSplit(source[yamlStart..lineStart], yamlStart, bodyStart);
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return _none;
    }

    /// <summary>True when the line [start, end) is exactly the fence, ignoring a trailing CR and trailing spaces or tabs.</summary>
    private static bool IsFenceLine(string source, int start, int end)
    {
        if (end > start && source[end - 1] == '\r')
        {
            end--;
        }

        return end - start >= _fence.Length
            && string.CompareOrdinal(source, start, _fence, 0, _fence.Length) == 0
            && source.AsSpan(start + _fence.Length, end - start - _fence.Length).TrimEnd(" \t").IsEmpty;
    }
}
