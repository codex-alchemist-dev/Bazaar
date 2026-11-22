namespace Bazaar.Install.Trust;

/// <summary>
/// The text rules for every signed or approved module field: ids, versions, publisher ids, key ids and permission
/// names. Only printable ASCII without spaces is accepted, so two names that look alike on an approval card are the
/// same bytes: no homoglyphs, no bidirectional controls, no padding, no line breaks in an audit row.
/// </summary>
public static class ModuleText
{
    /// <summary>The longest accepted field, in characters.</summary>
    public const int MaxTokenLength = 256;

    /// <summary>The most permissions one package may declare or one approval may hold.</summary>
    public const int MaxPermissions = 256;

    /// <summary>Whether <paramref name="value"/> is 1 to <see cref="MaxTokenLength"/> printable ASCII characters with no space.</summary>
    public static bool IsToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="value"/> is a SHA-256 digest in lowercase hex.</summary>
    public static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
