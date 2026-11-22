namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// Validation of every path that is written into a diskpart or PowerShell script. Scripts are text, so a quote or a line
/// break in a path would let it inject commands. A path must be absolute with a drive letter, plain printable ASCII (diskpart
/// reads its stdin in the OEM code page, so other characters would change meaning), and free of quotes, shell and wildcard
/// characters, control characters and relative segments.
/// </summary>
internal static class ScriptPaths
{
    private const string _forbidden = "\"'`<>|*?%$;&^!";

    /// <summary>Returns <paramref name="path"/> unchanged when valid; throws <see cref="ArgumentException"/> otherwise.</summary>
    public static string Validate(string path, string parameterName, bool requireVhdx = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path is empty.", parameterName);

        if (path.Length > 240)
            throw new ArgumentException("The path is too long.", parameterName);

        foreach (var c in path)
        {
            if (c < 0x20 || c > 0x7E || _forbidden.Contains(c))
                throw new ArgumentException("The path contains a character that is not allowed in a script.", parameterName);
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new ArgumentException("The path must be absolute with a drive letter.", parameterName);

        foreach (var segment in path.Split('\\'))
        {
            if (segment is "." or "..")
                throw new ArgumentException("The path must not contain relative segments.", parameterName);
        }

        if (requireVhdx && !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The container path must end in .vhdx.", parameterName);

        return path;
    }

    /// <summary>The folder form used for a mount: validated, without a trailing backslash (unless it is a drive root).</summary>
    public static string ValidateFolder(string path, string parameterName)
    {
        Validate(path, parameterName);
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }
}
