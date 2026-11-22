namespace Bazaar.ModulePermissions.Fs;

/// <summary>
/// The named roots a host gives modules: root name to absolute host directory. The host supplies it; a module only ever
/// names a root, and never sees or supplies a host path. A root name is one clean path segment such as <c>local</c> or
/// <c>cache</c>, the same name a permission scope uses (<c>fs:local/*</c>). The root directory itself may be a link (that is
/// the operator's choice); everything below it may not.
/// </summary>
public sealed class ModuleFsRoots
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);

    /// <summary>Builds the map. Throws <see cref="ArgumentException"/> for a bad name, a relative directory, a filesystem root, a duplicate name or directories that contain one another.</summary>
    public ModuleFsRoots(IEnumerable<KeyValuePair<string, string>> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var (name, directory) in roots)
        {
            if (!ModuleFsPathRules.IsValidSegment(name) || !ModuleTargetRules.IsValidTarget(name))
            {
                throw new ArgumentException("A root name must be one clean path segment.", nameof(roots));
            }

            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException("A root directory must be an absolute path.", nameof(roots));
            }

            var full = Path.GetFullPath(directory);
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            if (trimmed.Length == 0 || Path.EndsInDirectorySeparator(trimmed))
            {
                // What is left still ends in a separator only for a filesystem root ("/" or "C:\"): never a module root.
                throw new ArgumentException("A root directory must not be a filesystem root.", nameof(roots));
            }

            // Two names for the same files would make a grant on one name reach files approved under the other.
            foreach (var other in _roots.Values)
            {
                if (IsSameOrBelow(trimmed, other) || IsSameOrBelow(other, trimmed))
                {
                    throw new ArgumentException("Root directories must not contain one another.", nameof(roots));
                }
            }

            if (!_roots.TryAdd(name, trimmed))
            {
                throw new ArgumentException("A root name is given twice.", nameof(roots));
            }
        }
    }

    /// <summary>The root names, for the host's own use.</summary>
    public IReadOnlyCollection<string> Names => _roots.Keys;

    // Compared without case on every OS: a wrongly refused pair of roots is a configuration error the operator sees at
    // start, while a missed overlap on a case-insensitive disk would be silent.
    private static bool IsSameOrBelow(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal bool TryGetDirectory(string name, out string directory) => _roots.TryGetValue(name, out directory!);
}
