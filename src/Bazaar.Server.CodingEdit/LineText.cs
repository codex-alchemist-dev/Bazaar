using System.Text;

namespace Bazaar.Server.CodingEdit;

/// <summary>Text helpers shared by the edit tools: strict UTF-8, line splitting and a changed-line count.</summary>
internal static class LineText
{
    private static readonly UTF8Encoding _strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly UTF8Encoding _lenient = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Decodes bytes as UTF-8. Fails on invalid bytes, so a text edit never rewrites bytes it did not understand.</summary>
    public static bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            text = _strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>Encodes model text as UTF-8. Fails on unpaired surrogates, which model input may carry.</summary>
    public static bool TryEncode(string text, out byte[] bytes)
    {
        try
        {
            bytes = _strict.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Encodes text that came from <see cref="TryDecode"/> or from a validated edit. Never throws.</summary>
    public static byte[] EncodeValidated(string text) => _lenient.GetBytes(text);

    /// <summary>The SHA-256 (hex) of validated text.</summary>
    public static string Sha256(string text) => ContentHash.Sha256Hex(EncodeValidated(text));

    /// <summary>
    /// Splits text into lines. A single trailing newline ends the last line and does not add an empty one, so
    /// "a\nb\n" has two lines and reports <paramref name="trailingNewline"/> as true.
    /// </summary>
    public static List<string> Lines(string text, out bool trailingNewline)
    {
        if (text.Length == 0)
        {
            trailingNewline = false;
            return [];
        }

        var parts = text.Split('\n').ToList();
        trailingNewline = text[^1] == '\n';
        if (trailingNewline)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    /// <summary>Joins lines back into text, restoring the trailing newline when there was one.</summary>
    public static string Join(IReadOnlyList<string> lines, bool trailingNewline)
    {
        if (lines.Count == 0)
        {
            return "";
        }

        var body = string.Join('\n', lines);
        return trailingNewline ? body + "\n" : body;
    }

    /// <summary>The lines a replacement stands for. Empty text is zero lines, and one trailing newline does not add a line.</summary>
    public static List<string> ReplacementLines(string replacement)
    {
        if (replacement.Length == 0)
        {
            return [];
        }

        var parts = replacement.Split('\n').ToList();
        if (replacement[^1] == '\n')
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    /// <summary>
    /// The number of lines removed or added by a change, after the common prefix and suffix of lines are removed. A change that
    /// only moves the trailing newline touches no line, but it still counts as one. A missing <paramref name="before"/> counts as
    /// an empty file.
    /// </summary>
    public static int ChangedLines(string? before, string after)
    {
        var a = before is null ? [] : Lines(before, out _);
        var b = Lines(after, out _);

        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix
            && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
        {
            suffix++;
        }

        var changed = Math.Max(a.Count - prefix - suffix, b.Count - prefix - suffix);
        return changed == 0 && before != after ? 1 : changed;
    }
}
