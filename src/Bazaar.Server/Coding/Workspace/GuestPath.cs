namespace Bazaar.Server.Coding.Workspace;

/// <summary>
/// Path jail for guest paths. A relative path is joined under a root, and anything that could leave the root is refused.
/// </summary>
/// <remarks>
/// This is a lexical check only. Symlink checks are out of scope for the in-memory fake; the real guest agent must
/// resolve links inside the worktree (doc 16 guest protocol). Output uses forward slashes after the root.
/// </remarks>
public static class GuestPath
{
    /// <summary>
    /// Joins <paramref name="relative"/> under <paramref name="root"/> and returns the normalized path.
    /// "." and empty segments are dropped. A relative value of "." returns the root.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The root or relative value is null, blank or whitespace; relative is absolute (starts with a slash or backslash,
    /// or contains a colon); a segment is "..", or the value contains a NUL character.
    /// </exception>
    public static string Resolve(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        if (relative[0] is '/' or '\\' || relative.Contains(':') || relative.Contains('\0'))
        {
            throw new ArgumentException("Guest paths must be relative to the worktree root and free of drive or device markers.", nameof(relative));
        }

        var segments = new List<string>();
        foreach (var segment in relative.Split(['/', '\\']))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new ArgumentException("Guest paths may not contain '..' segments.", nameof(relative));
            }

            segments.Add(segment);
        }

        var prefix = root.TrimEnd('/', '\\');
        if (segments.Count == 0)
        {
            return prefix.Length == 0 ? "/" : prefix;
        }

        return prefix + "/" + string.Join('/', segments);
    }
}
