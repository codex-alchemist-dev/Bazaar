namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// The canonical form of a Windows volume GUID. The canonical form is 36 lowercase characters, no braces and no path
/// decoration (<c>0b6f2a1e-...</c>). It is what the descriptor stores as the volume id, what the status pipe carries and
/// what every comparison uses. The decorated form <c>\\?\Volume{guid}\</c> exists only at the WMI and kernel32 edges.
/// </summary>
internal static class VolumeGuid
{
    private const string _volumePrefix = @"\\?\Volume{";

    /// <summary>Returns the canonical form, or throws <see cref="FormatException"/>.</summary>
    public static string Canonicalize(string text) =>
        TryCanonicalize(text, out var canonical) ? canonical : throw new FormatException("Not a volume GUID.");

    public static bool TryCanonicalize(string? text, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.StartsWith(_volumePrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s[(_volumePrefix.Length - 1)..];
            if (s.EndsWith('\\'))
                s = s[..^1];
        }

        if (s.Length > 2 && s[0] == '{' && s[^1] == '}')
            s = s[1..^1];

        if (s.Length != 36 || !Guid.TryParseExact(s, "D", out var guid))
            return false;

        canonical = guid.ToString("D");
        return true;
    }

    /// <summary>True when the text is already exactly in canonical form.</summary>
    public static bool IsCanonical(string? text) =>
        TryCanonicalize(text, out var canonical) && string.Equals(canonical, text, StringComparison.Ordinal);

    /// <summary>The <c>\\?\Volume{guid}\</c> form Win32_EncryptableVolume uses as its DeviceID.</summary>
    public static string ToVolumePath(string canonical) => $@"\\?\Volume{{{canonical}}}\";
}
