namespace Bazaar.Core;

/// <summary>Shared validation for the <c>kind:id</c> text forms of <see cref="Principal"/> and <see cref="Scope"/>.</summary>
internal static class TextForm
{
    /// <summary>The longest accepted identifier or path segment, in UTF-16 code units.</summary>
    internal const int MaxSegmentLength = 128;

    /// <summary>
    /// True when <paramref name="segment"/> is a valid identifier: 1 to <see cref="MaxSegmentLength"/> characters,
    /// with no control characters, whitespace, ':' or '/'. The separators stay out of identifiers so the first
    /// ':' of a text form always ends the kind and a '/' always splits a scope path.
    /// </summary>
    internal static bool IsValidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length is 0 or > MaxSegmentLength)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is ':' or '/')
            {
                return false;
            }
        }

        return true;
    }
}
