namespace Bazaar.Server.Components.Backup;

/// <summary>
/// Chooses which files a backup may carry (docs/design/33-backup-dr-and-export.md, "Cookie profile exclusion [PROPOSED]").
/// With <c>security.backup.include_cookie_profiles</c> false (the default), mounted cookie-profile files, canary decoys and
/// browser profile directories are left out of offsite uploads and Soul-folder snapshots. Matching is case-insensitive.
/// </summary>
public sealed class BackupFileFilter
{
    /// <summary>Suffix of every mounted cookie-profile file and canary decoy (<c>canary-&lt;id&gt;.session.json</c>).</summary>
    private const string _sessionFileSuffix = ".session.json";

    /// <summary>Directory names that hold a browser profile. Any path segment with one of these names is excluded.</summary>
    private static readonly string[] _browserProfileDirectories = ["browser-profile", "browser-profiles", "chromium-profile", "cookie-profiles"];

    public BackupFileFilter(bool includeCookieProfiles = false) => IncludeCookieProfiles = includeCookieProfiles;

    /// <summary>True only when the operator (with owner approval, doc 15) included cookie profiles in backups.</summary>
    public bool IncludeCookieProfiles { get; }

    /// <summary>
    /// The same rules as restic <c>--exclude</c> patterns. Patterns without a path separator match a file or directory
    /// name, so they reach the mount and profile folders at any depth. Empty when cookie profiles are included.
    /// </summary>
    public IReadOnlyList<string> ResticExcludePatterns =>
        IncludeCookieProfiles ? [] : [.. _browserProfileDirectories.Prepend("*" + _sessionFileSuffix)];

    /// <summary>Whether a path relative to a backup root is cookie material: a session file or inside a browser profile directory.</summary>
    /// <param name="relativePath">A path relative to the backup root. Either separator is accepted.</param>
    public static bool IsCookieProfilePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;
        if (segments[^1].EndsWith(_sessionFileSuffix, StringComparison.OrdinalIgnoreCase))
            return true;
        return segments.Any(segment => _browserProfileDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether a file at this relative path may be backed up under the current flag.</summary>
    public bool ShouldInclude(string relativePath) => IncludeCookieProfiles || !IsCookieProfilePath(relativePath);

    /// <summary>
    /// Lists the files under <paramref name="rootDirectory"/> that may be backed up, as sorted relative paths with '/'
    /// separators. Used by the Soul-folder snapshot (WP6) and by tests; the offsite upload applies the same rules through
    /// <see cref="ResticExcludePatterns"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The root directory does not exist.</exception>
    public IReadOnlyList<string> SelectIncludedFiles(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException("Backup source directory is missing.");

        var selected = new List<string>();
        foreach (var file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');
            if (ShouldInclude(relative))
                selected.Add(relative);
        }

        selected.Sort(StringComparer.Ordinal);
        return selected;
    }
}
