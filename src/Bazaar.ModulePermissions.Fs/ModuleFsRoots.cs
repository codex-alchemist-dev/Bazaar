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

    /// <summary>Builds the map. Throws <see cref="ArgumentException"/> for a bad name, a relative directory or a duplicate name.</summary>
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
            if (!_roots.TryAdd(name, trimmed.Length == 0 ? full : trimmed))
            {
                throw new ArgumentException("A root name is given twice.", nameof(roots));
            }
        }
    }

    /// <summary>The root names, for the host's own use.</summary>
    public IReadOnlyCollection<string> Names => _roots.Keys;

    internal bool TryGetDirectory(string name, out string directory) => _roots.TryGetValue(name, out directory!);
}
