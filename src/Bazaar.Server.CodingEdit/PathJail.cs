namespace Bazaar.Server.CodingEdit;

/// <summary>The jail's verdict on one path.</summary>
/// <param name="Allowed">True when the path may be used.</param>
/// <param name="Path">The normalized worktree-relative path when allowed; empty otherwise.</param>
/// <param name="Reason">The refusal reason when not allowed; null otherwise.</param>
/// <param name="Detail">A short note for the refusal.</param>
public sealed record JailDecision(bool Allowed, string Path, EditRejectReason? Reason, string? Detail)
{
    /// <summary>A path that may be used.</summary>
    public static JailDecision Allow(string path) => new(true, path, null, null);

    /// <summary>A path that may not be used.</summary>
    public static JailDecision Refuse(EditRejectReason reason, string detail) => new(false, "", reason, detail);
}

/// <summary>
/// Path jail for edit tools (doc 17, "Behaviour and edge cases"). It refuses <c>..</c>, absolute paths, drive and device markers,
/// NUL, and symlinks that leave the worktree.
/// </summary>
/// <remarks>
/// The lexical rules here mirror <c>GuestPath</c> in Bazaar.Server.Coding, which this module does not reference because it needs typed refusal reasons instead of exceptions. The rules are stricter where
/// Windows would alias names: a segment ending in a dot or space, and reserved device names (CON, NUL, COM1 and so on), are refused.
/// Symlinks are checked through <see cref="IWorktreeFiles.ResolveRealPathAsync"/>, so a store that cannot resolve a link refuses the path.
/// </remarks>
public static class PathJail
{
    private static readonly HashSet<string> _deviceNames = BuildDeviceNames();

    private const string _forbiddenCharacters = "<>\"|?*";

    /// <summary>Checks the path without touching the worktree. Model input never throws here.</summary>
    public static JailDecision Lexical(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return JailDecision.Refuse(EditRejectReason.Malformed, "the path is empty");
        }

        if (relative.Contains('\0'))
        {
            return JailDecision.Refuse(EditRejectReason.PathEscape, "the path contains a NUL character");
        }

        if (relative[0] is '/' or '\\' || relative.Contains(':'))
        {
            return JailDecision.Refuse(EditRejectReason.PathEscape, "the path is absolute or has a drive or device marker");
        }

        if (relative.Any(c => c < ' ' || _forbiddenCharacters.Contains(c)))
        {
            return JailDecision.Refuse(EditRejectReason.PathEscape, "the path has a character a worktree path may not use");
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
                return JailDecision.Refuse(EditRejectReason.PathEscape, "the path contains a '..' segment");
            }

            if (segment[^1] is '.' or ' ')
            {
                return JailDecision.Refuse(EditRejectReason.PathEscape, "a path segment ends with a dot or a space");
            }

            if (_deviceNames.Contains(segment.Split('.')[0]))
            {
                return JailDecision.Refuse(EditRejectReason.PathEscape, "the path uses a reserved device name");
            }

            segments.Add(segment);
        }

        return segments.Count == 0
            ? JailDecision.Refuse(EditRejectReason.Malformed, "the path names the worktree root, not a file")
            : JailDecision.Allow(string.Join('/', segments));
    }

    /// <summary>
    /// Checks the path lexically, then resolves its symlinks through the store. A link chain that leaves the worktree, or that the
    /// store cannot resolve, is refused as <see cref="EditRejectReason.PathEscape"/>.
    /// </summary>
    public static async Task<JailDecision> AuthorizeAsync(IWorktreeFiles files, string? relative, CancellationToken ct)
    {
        var lexical = Lexical(relative);
        if (!lexical.Allowed)
        {
            return lexical;
        }

        var real = await files.ResolveRealPathAsync(lexical.Path, ct);
        if (real is null)
        {
            return JailDecision.Refuse(EditRejectReason.PathEscape, "a symlink in the path leaves the worktree or cannot be resolved");
        }

        if (real.Length == 0)
        {
            return JailDecision.Refuse(EditRejectReason.Malformed, "the path resolves to the worktree root, not a file");
        }

        // The store is trusted to stay inside the root, but the resolved target is checked again as defence in depth.
        var recheck = Lexical(real);
        if (!recheck.Allowed)
        {
            return JailDecision.Refuse(EditRejectReason.PathEscape, "a symlink target is not a plain worktree path");
        }

        return lexical;
    }

    private static HashSet<string> BuildDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 1; i <= 9; i++)
        {
            names.Add("COM" + i);
            names.Add("LPT" + i);
        }

        return names;
    }
}
