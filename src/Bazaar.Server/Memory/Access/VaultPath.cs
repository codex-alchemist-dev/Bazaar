using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.Memory.Access;

/// <summary>
/// A normalized, relative, forward-slash path inside the Vault (docs/design/18-vault.md). Construction rejects
/// anything that could leave the vault root or name a different file than it reads: absolute paths, backslashes,
/// NUL and other control characters, colons (drive letters and alternate streams), empty segments and "." or ".."
/// segments. Matching in <see cref="VaultPathAccess"/> is ordinal and case-sensitive.
/// </summary>
public sealed record VaultPath
{
    private VaultPath(string value) => Value = value;

    /// <summary>The path as written: forward slashes, no leading or trailing slash.</summary>
    public string Value { get; }

    /// <summary>The path split into its segments, in order. Never empty.</summary>
    public IReadOnlyList<string> Segments => Value.Split('/');

    /// <summary>Parses a path, throwing <see cref="ArgumentException"/> when it is not a normalized relative path.</summary>
    public static VaultPath Parse(string path)
    {
        var error = Validate(path);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(path));
        }

        return new VaultPath(path);
    }

    /// <summary>Parses a path without throwing. Returns false for null, absolute, traversing or malformed input.</summary>
    public static bool TryParse([NotNullWhen(true)] string? path, [NotNullWhen(true)] out VaultPath? result)
    {
        if (path is not null && Validate(path) is null)
        {
            result = new VaultPath(path);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>The path string, with forward slashes.</summary>
    public override string ToString() => Value;

    /// <summary>
    /// Checks a single identifier (an agent or member id) that is used as one path segment.
    /// Identifiers must be valid segments and must not contain a slash.
    /// </summary>
    internal static bool IsValidIdentifier(string identifier) =>
        identifier is not null && !identifier.Contains('/', StringComparison.Ordinal) && Validate(identifier) is null;

    /// <summary>Returns the reason a path is not valid, or null when it is valid.</summary>
    internal static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "Vault path must not be empty.";
        }

        if (path[0] == '/')
        {
            return "Vault path must be relative, not absolute.";
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return "Vault path must use forward slashes only.";
        }

        if (path.Any(char.IsControl))
        {
            return "Vault path must not contain control characters or NUL.";
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            return "Vault path must not contain colons (drive letters or alternate streams).";
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                return "Vault path must not contain empty segments.";
            }

            if (segment is "." or "..")
            {
                return "Vault path must not contain '.' or '..' segments.";
            }
        }

        return null;
    }
}
