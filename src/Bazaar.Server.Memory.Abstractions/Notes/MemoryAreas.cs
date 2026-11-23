using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Memory.Notes;

/// <summary>Result of checking a note path against the area rules.</summary>
/// <param name="IsValid">True when the path is an allowed location for the kind.</param>
/// <param name="Scope">The scope the path is in, when valid.</param>
/// <param name="Owner">The agent or project slug for private and project paths, otherwise null.</param>
/// <param name="HiveKind">The hive kind folder for hive paths, otherwise null.</param>
/// <param name="Reason">A short code for an invalid path, otherwise null.</param>
public sealed record MemoryAreaCheck(bool IsValid, MemoryScope? Scope, string? Owner, string? HiveKind, string? Reason)
{
    /// <summary>A rejected path with its reason code.</summary>
    public static MemoryAreaCheck Invalid(string reason) => new(false, null, null, null, reason);

    /// <summary>An accepted path.</summary>
    public static MemoryAreaCheck Valid(MemoryScope scope, string? owner, string? hiveKind) =>
        new(true, scope, owner, hiveKind, null);
}

/// <summary>
/// Area rules for memory notes (docs/design/08-memory-system.md, section 1). Pure: no I/O.
/// Private notes live under <c>agents/&lt;agent&gt;/memory/&lt;folder&gt;/</c>, project notes under
/// <c>projects/&lt;slug&gt;/memory/&lt;folder&gt;/</c>, company notes under <c>company/memory/&lt;folder&gt;/</c>,
/// and hive copies under <c>hive/&lt;hive-kind&gt;/</c>. Episodic notes add a <c>YYYY-MM-DD</c> folder.
/// </summary>
public static partial class MemoryAreas
{
    /// <summary>Longest path accepted, in characters.</summary>
    public const int MaxPathLength = 512;

    /// <summary>Hive kinds a copy may carry (doc 08, section 6; setting <c>hive.allowed_kinds</c>).</summary>
    public static readonly IReadOnlyList<string> HiveKinds = ["procedure", "failure", "technical-fact"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^[0-9A-Za-z]{26}$")]
    private static partial Regex UlidRegex();

    /// <summary>The folder under the scope root that holds a kind, or null for hive notes.</summary>
    public static string? FolderFor(MemoryKind kind) => kind switch
    {
        MemoryKind.Episodic => "episodes",
        MemoryKind.Semantic => "semantic",
        MemoryKind.Procedural => "lessons",
        MemoryKind.Failure => "failures",
        MemoryKind.Preference => "preferences",
        MemoryKind.Working => "working",
        _ => null,
    };

    /// <summary>
    /// The expected directory for a kind in a scope, without a trailing slash, or null when the kind
    /// is not allowed there. Hive notes need <paramref name="hiveKind"/>; private and project notes need
    /// <paramref name="owner"/>. Company notes ignore the owner.
    /// </summary>
    public static string? ExpectedDirectory(MemoryKind kind, MemoryScope scope, string? owner = null, string? hiveKind = null)
    {
        if (scope == MemoryScope.Hive)
        {
            return kind == MemoryKind.Hive && hiveKind is not null && HiveKinds.Contains(hiveKind, StringComparer.Ordinal)
                ? "hive/" + hiveKind
                : null;
        }

        if (kind == MemoryKind.Hive)
        {
            return null;
        }

        var folder = FolderFor(kind);
        if (folder is null)
        {
            return null;
        }

        if (scope != MemoryScope.Company && !IsSlug(owner))
        {
            return null;
        }

        return scope switch
        {
            MemoryScope.Private => "agents/" + owner + "/memory/" + folder,
            MemoryScope.Project => "projects/" + owner + "/memory/" + folder,
            _ => "company/memory/" + folder,
        };
    }

    /// <summary>
    /// Checks a note path (relative to the vault root) against the area for <paramref name="kind"/>.
    /// Rejects absolute paths, traversal, backslashes, control characters, non-Markdown names and
    /// file names that are not slugs (episodic notes use a ULID).
    /// </summary>
    public static MemoryAreaCheck CheckPath(string path, MemoryKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length is 0 or > MaxPathLength)
        {
            return MemoryAreaCheck.Invalid("path-length");
        }

        if (path.StartsWith('/')
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal)
            || !path.EndsWith(".md", StringComparison.Ordinal)
            || path.Any(c => char.IsControl(c)))
        {
            return MemoryAreaCheck.Invalid("unsafe-path");
        }

        var parts = path.Split('/');
        if (parts.Any(p => p is "." or ".."))
        {
            return MemoryAreaCheck.Invalid("unsafe-path");
        }

        var dirParts = parts[..^1];
        if (dirParts.Length == 0)
        {
            return MemoryAreaCheck.Invalid("outside-memory-areas");
        }

        var fileStem = parts[^1][..^3];

        MemoryScope scope;
        string? owner = null;
        string? hiveKind = null;
        switch (dirParts[0])
        {
            case "agents" when dirParts.Length >= 3 && dirParts[2] == "memory":
                scope = MemoryScope.Private;
                owner = dirParts[1];
                break;
            case "projects" when dirParts.Length >= 3 && dirParts[2] == "memory":
                scope = MemoryScope.Project;
                owner = dirParts[1];
                break;
            case "company" when dirParts.Length >= 2 && dirParts[1] == "memory":
                scope = MemoryScope.Company;
                break;
            case "hive" when dirParts.Length == 2:
                scope = MemoryScope.Hive;
                hiveKind = dirParts[1];
                break;
            default:
                return MemoryAreaCheck.Invalid("outside-memory-areas");
        }

        if (scope is MemoryScope.Private or MemoryScope.Project && !IsSlug(owner))
        {
            return MemoryAreaCheck.Invalid("bad-owner");
        }

        var expected = ExpectedDirectory(kind, scope, owner, hiveKind);
        if (expected is null)
        {
            return MemoryAreaCheck.Invalid("kind-not-allowed-in-area");
        }

        var dir = string.Join('/', dirParts);
        if (kind == MemoryKind.Episodic)
        {
            var prefix = expected + "/";
            if (!dir.StartsWith(prefix, StringComparison.Ordinal)
                || !IsEpisodeDate(dir[prefix.Length..])
                || !UlidRegex().IsMatch(fileStem))
            {
                return MemoryAreaCheck.Invalid("wrong-area");
            }
        }
        else
        {
            if (!string.Equals(dir, expected, StringComparison.Ordinal))
            {
                return MemoryAreaCheck.Invalid("wrong-area");
            }

            if (!IsSlug(fileStem))
            {
                return MemoryAreaCheck.Invalid("bad-file-name");
            }
        }

        return MemoryAreaCheck.Valid(scope, owner, hiveKind);
    }

    private static bool IsSlug(string? text) => text is not null && SlugRegex().IsMatch(text);

    private static bool IsEpisodeDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
