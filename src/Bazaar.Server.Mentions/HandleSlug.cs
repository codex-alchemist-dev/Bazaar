using System.Globalization;

namespace Bazaar.Server.Mentions;

/// <summary>
/// The rules for a principal handle, the name typed after "@" (pure, no I/O). A handle is 2 to 32 characters: a lowercase ASCII
/// letter or digit, then lowercase ASCII letters, digits, "_" or "-". "here" and "all" are the broadcast words and are never
/// handles. The <c>principal_handle</c> table checks the same shape (<c>mentions/0001</c>).
/// </summary>
public static class HandleSlug
{
    /// <summary>The shortest handle.</summary>
    public const int MinLength = 2;

    /// <summary>The longest handle.</summary>
    public const int MaxLength = 32;

    private static readonly string[] _reserved = ["here", "all"];

    /// <summary>True when <paramref name="handle"/> is one of the broadcast words, which no principal may hold.</summary>
    public static bool IsReserved(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Array.IndexOf(_reserved, handle) >= 0;
    }

    /// <summary>True when <paramref name="handle"/> may be stored as it is: the right shape, lowercase and not reserved.</summary>
    public static bool IsValid(string? handle)
    {
        if (handle is null || handle.Length < MinLength || handle.Length > MaxLength)
            return false;
        if (!IsLetterOrDigit(handle[0]))
            return false;
        foreach (var c in handle)
        {
            if (!IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        return !IsReserved(handle);
    }

    /// <summary>
    /// Trims surrounding white space and lowercases ASCII letters, so "Ana" and "ana" name one handle. The result still has to
    /// pass <see cref="IsValid"/>.
    /// </summary>
    public static string Normalize(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return ToLowerAsciiOnly(handle.Trim());
    }

    /// <summary>
    /// Turns a display name into a handle: ASCII upper case becomes lower case, every run of characters other than ASCII letters,
    /// digits and "_" becomes one "-", "-" and "_" are trimmed from both ends, and the result is cut to <see cref="MaxLength"/>
    /// and trimmed again. Returns null when fewer than <see cref="MinLength"/> characters remain.
    /// </summary>
    /// <remarks>The result can be a reserved word ("All" gives "all"); callers treat that as a taken handle and add a suffix.</remarks>
    public static string? FromName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var buffer = new char[name.Length];
        var length = 0;
        var pendingDash = false;
        foreach (var raw in name)
        {
            var c = raw is >= 'A' and <= 'Z' ? (char)(raw + ('a' - 'A')) : raw;
            if (IsLetterOrDigit(c) || c == '_')
            {
                if (pendingDash)
                    buffer[length++] = '-';
                pendingDash = false;
                buffer[length++] = c;
            }
            else
            {
                // A run before the first kept character is dropped here; one at the end is never written.
                pendingDash = length > 0;
            }
        }

        var slug = new string(buffer, 0, length).Trim('-', '_');
        if (slug.Length > MaxLength)
            slug = slug[..MaxLength].TrimEnd('-', '_');
        return slug.Length < MinLength ? null : slug;
    }

    /// <summary>
    /// The <paramref name="n"/>th candidate for a slug: the slug itself for 1, otherwise the slug with "-n" appended, cut first so
    /// the whole handle stays within <see cref="MaxLength"/>.
    /// </summary>
    public static string WithSuffix(string slug, int n)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        if (n == 1)
            return slug;

        var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
        var room = MaxLength - suffix.Length;
        var stem = slug.Length > room ? slug[..room] : slug;
        return stem.TrimEnd('-', '_') + suffix;
    }

    private static bool IsLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static string ToLowerAsciiOnly(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
        });
}
