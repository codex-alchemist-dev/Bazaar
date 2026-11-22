using System.Globalization;
using System.Text;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// What counts as one reaction emoji (docs/design/01, "Emoji form"): a single grapheme cluster, within the byte limit, with no
/// control characters or whitespace, and not plain ASCII text (a keycap sequence such as 1 + U+FE0F + U+20E3 is allowed).
/// </summary>
public static class EmojiRules
{
    /// <summary>Why an emoji was refused, or null when it is acceptable.</summary>
    public static string? Refusal(string? emoji, int maxBytes)
    {
        if (string.IsNullOrEmpty(emoji))
            return "An emoji is required.";
        if (Encoding.UTF8.GetByteCount(emoji) > maxBytes)
            return "The emoji is too long.";

        var elements = StringInfo.GetTextElementEnumerator(emoji);
        var count = 0;
        while (elements.MoveNext())
            count++;
        if (count != 1)
            return "A reaction is exactly one emoji.";

        var allAscii = true;
        var keycap = false;
        foreach (var rune in emoji.EnumerateRunes())
        {
            if (rune.Value == 0xFFFD || Rune.IsWhiteSpace(rune))
                return "The emoji holds a character that is not allowed.";
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return "The emoji holds a character that is not allowed.";
            }

            allAscii &= rune.IsAscii;
            keycap |= rune.Value == 0x20E3;
        }

        return allAscii && !keycap ? "A reaction is an emoji, not text." : null;
    }
}
