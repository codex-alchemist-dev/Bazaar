using System.Text.RegularExpressions;

namespace Bazaar.Server.DataVolume.Linux;

/// <summary>Argument checks for every value the operator puts on a command line. Arguments are never run through a shell, but a bad path is still refused early.</summary>
internal static partial class LinuxPaths
{
    private const int _maxPathLength = 4096;

    /// <summary>Requires an absolute path with no <c>..</c> segment and no NUL, CR or LF.</summary>
    public static string Require(string? path, string what)
    {
        if (string.IsNullOrEmpty(path) || path.Length > _maxPathLength)
            throw new ArgumentException($"{what} must be a path of 1 to {_maxPathLength} characters.", what);
        if (path[0] != '/')
            throw new ArgumentException($"{what} must be an absolute path.", what);
        if (path.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0)
            throw new ArgumentException($"{what} must not contain a NUL or a line break.", what);
        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
                throw new ArgumentException($"{what} must not contain '..'.", what);
        }

        return path;
    }

    /// <summary>The service account name: a conservative Linux user name, never starting with <c>-</c>.</summary>
    public static string RequireAccount(string? account)
    {
        if (string.IsNullOrEmpty(account) || !AccountPattern().IsMatch(account))
            throw new ArgumentException("The service account must be a plain Linux user name.", nameof(account));
        return account;
    }

    /// <summary>Removes trailing slashes, keeping the root.</summary>
    public static string Normalise(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex AccountPattern();
}
