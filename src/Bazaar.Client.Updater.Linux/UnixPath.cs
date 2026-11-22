namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// Path handling for the Linux install layout. The updater only ever names Linux paths, so it uses '/' on every host
/// (the solution also builds and tests on Windows, where <see cref="Path"/> would switch to backslashes).
/// </summary>
internal static class UnixPath
{
    public static bool IsRooted(string path) => path.StartsWith('/');

    public static string Combine(string left, string right)
    {
        if (IsRooted(right))
        {
            return right;
        }

        return left.EndsWith('/') ? left + right : left + "/" + right;
    }

    public static string? GetDirectoryName(string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        int slash = trimmed.LastIndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        return slash == 0 ? (trimmed.Length == 1 ? null : "/") : trimmed[..slash];
    }

    public static string GetFileName(string path)
    {
        string trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}
