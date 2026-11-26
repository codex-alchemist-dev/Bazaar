using Markdig.Helpers;
using Markdig.Parsers;

namespace Bazaar.Server.Vault.Markdown;

/// <summary>
/// Inline parser for <c>[[target#anchor|alias]]</c> and <c>![[...]]</c> (doc 18, "Links"). It is inserted ahead of
/// Markdig's link parser, so a wiki-link is never read as a Markdown link. A match must sit on one line and contain no
/// <c>[</c>, <c>]</c> or line break inside the brackets; anything else is left to Markdig.
/// </summary>
internal sealed class WikiLinkInlineParser : InlineParser
{
    public WikiLinkInlineParser()
    {
        OpeningCharacters = ['[', '!'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        // Offsets are absolute: slice.Text is the whole source and slice.End is the last index of the current line.
        var text = slice.Text;
        var last = slice.End;
        var open = slice.Start;
        var kind = WikiLinkKind.Link;
        if (text[open] == '!')
        {
            kind = WikiLinkKind.Embed;
            open++;
        }

        if (open + 1 > last || text[open] != '[' || text[open + 1] != '[')
        {
            return false;
        }

        var innerStart = open + 2;
        var close = innerStart;
        while (close <= last && text[close] != ']')
        {
            if (text[close] is '[' or '\r' or '\n')
            {
                return false;
            }

            close++;
        }

        if (close + 1 > last || text[close + 1] != ']')
        {
            return false;
        }

        // Markdig gives inline text per leaf block, so slice offsets are not source offsets once containers
        // (blockquotes, list items) strip their prefixes. A link never crosses a line, so its span is contiguous
        // in the source and only the start needs mapping.
        var sliceStart = slice.Start;
        var sliceEnd = close + 2;
        var sourceStart = processor.GetSourcePosition(sliceStart);
        var link = Create(kind, text.Substring(innerStart, close - innerStart), sourceStart, sliceEnd - sliceStart);
        if (link is null)
        {
            return false;
        }

        processor.Inline = new WikiLinkInline(link);
        slice.Start = sliceEnd;
        return true;
    }

    /// <summary>Splits the text between <c>[[</c> and <c>]]</c>. Returns null when there is no target and no heading or block anchor.</summary>
    private static WikiLink? Create(WikiLinkKind kind, string inner, int start, int length)
    {
        var pipe = inner.IndexOf('|');
        var left = pipe < 0 ? inner : inner[..pipe];
        var alias = pipe < 0 ? null : NonEmpty(inner[(pipe + 1)..].Trim());

        var hash = left.IndexOf('#');
        var target = (hash < 0 ? left : left[..hash]).Trim();
        var anchorKind = WikiLinkAnchorKind.None;
        string? anchor = null;
        if (hash >= 0)
        {
            var raw = left[(hash + 1)..].Trim();
            if (raw.StartsWith('^'))
            {
                anchor = NonEmpty(raw[1..].Trim());
                anchorKind = anchor is null ? WikiLinkAnchorKind.None : WikiLinkAnchorKind.Block;
            }
            else
            {
                anchor = NonEmpty(raw);
                anchorKind = anchor is null ? WikiLinkAnchorKind.None : WikiLinkAnchorKind.Heading;
            }
        }

        if (target.Length == 0 && anchorKind == WikiLinkAnchorKind.None)
        {
            return null;
        }

        return new WikiLink(kind, target, anchorKind, anchor, alias, start, length);
    }

    private static string? NonEmpty(string value) => value.Length == 0 ? null : value;
}
