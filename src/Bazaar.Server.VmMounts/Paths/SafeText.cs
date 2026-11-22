namespace Bazaar.Server.VmMounts.Paths;

/// <summary>
/// Makes a guest-supplied or file-system-supplied name safe to put in an audit row, an exception message or a result. Control and
/// non-ASCII characters become <c>?</c> (no log forging, no bidirectional tricks) and the text is cut to a fixed length.
/// </summary>
internal static class SafeText
{
    public const int MaxLength = 80;

    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var length = Math.Min(value.Length, MaxLength);
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
            buffer[i] = value[i] is >= ' ' and <= '~' ? value[i] : '?';
        return value.Length > MaxLength ? new string(buffer) + "..." : new string(buffer);
    }
}
