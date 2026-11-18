using System.Text;

namespace Bazaar.Client.Athanor;

/// <summary>
/// Turns a raw path typed or sent by a client into the normalised, workspace-relative form that doc 52 §5 requires.
/// Backslashes become forward slashes, repeated slashes collapse, <c>.</c> segments drop, and <c>..</c> resolves
/// only while it stays inside the root. Encoded forms (<c>%2e%2e</c>) are not decoded, so they stay literal names.
/// The server repeats this check; the client never relies on its own result alone.
/// </summary>
public static class EditorPathNormalizer
{
    /// <summary>The longest raw path accepted, in characters (doc 52 §5 bounds the path length).</summary>
    public const int MaxPathLength = 1024;

    /// <summary>
    /// Normalises <paramref name="raw"/>. Returns false when the path is empty, escapes the root, is rooted on a
    /// drive or UNC share, contains a control character (NUL included), or is longer than <see cref="MaxPathLength"/>.
    /// </summary>
    public static bool TryNormalize(string raw, out string normalized)
    {
        normalized = string.Empty;
        if (raw is null || raw.Length == 0 || raw.Length > MaxPathLength)
        {
            return false;
        }

        foreach (var c in raw)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        var slashed = raw.Replace('\\', '/');

        // UNC shares (\\server\share, //server/share) are never workspace paths.
        if (slashed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = slashed.Split('/');

        // A drive letter (C: or C:/...) at the start of the path is rejected, after any leading slashes.
        var first = segments.FirstOrDefault(s => s.Length > 0) ?? string.Empty;
        if (first.Length >= 2 && char.IsAsciiLetter(first[0]) && first[1] == ':')
        {
            return false;
        }

        // A colon anywhere is refused: it also names NTFS alternate data streams (file.txt:stream).
        if (slashed.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var stack = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        return false;
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                default:
                    stack.Add(segment);
                    continue;
            }
        }

        if (stack.Count == 0)
        {
            return false;
        }

        normalized = string.Join('/', stack);
        return true;
    }
}
