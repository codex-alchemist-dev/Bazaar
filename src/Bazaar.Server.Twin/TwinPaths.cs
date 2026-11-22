using System.Text.RegularExpressions;

namespace Bazaar.Server.Twin;

/// <summary>Vault paths and name rules for Twin prose pages (doc 25 Twin storage, doc 18 folder <c>twin/</c>).</summary>
public static partial class TwinPaths
{
    /// <summary>The Vault folder holding every Twin page.</summary>
    public const string Folder = "twin";

    /// <summary>The Vault path of a page: <c>twin/&lt;project&gt;/&lt;page&gt;.md</c>.</summary>
    /// <exception cref="ArgumentException">A name is not a single safe path segment.</exception>
    public static string PagePath(string project, string page)
    {
        ValidateSegment(project, nameof(project));
        ValidateSegment(page, nameof(page));
        return $"{Folder}/{project}/{page}.md";
    }

    /// <summary>A path segment: lowercase letters, digits and single hyphens. Nothing else, so no separators and no dots.</summary>
    public static void ValidateSegment(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (!SegmentPattern().IsMatch(value))
        {
            throw new ArgumentException("Must be lowercase letters, digits and single hyphens.", paramName);
        }
    }

    /// <summary>Front matter titles may not contain # | ^ : %% [[ ]] (doc 18), nor a line break.</summary>
    public static void ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.IndexOfAny(['#', '|', '^', ':', '\r', '\n']) >= 0 || title.Contains("%%", StringComparison.Ordinal)
            || title.Contains("[[", StringComparison.Ordinal) || title.Contains("]]", StringComparison.Ordinal))
        {
            throw new ArgumentException("Title contains a character that front matter forbids (# | ^ : %% [[ ]]).", nameof(title));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
