using System.Text.RegularExpressions;
using Bazaar.Server.Components;

namespace Bazaar.Server.Data.Migrations;

/// <summary>
/// Migration namespaces (docs/design/BAZAAR-COMPONENTS.md section 5). Each namespace is a folder under <c>Data/Sql</c> and owns its
/// own numbered files. <c>common</c> is the shared, frozen namespace and always applies first. Component namespaces apply in
/// dependency order; any other namespace applies after them in name order.
/// </summary>
public static partial class MigrationNamespaces
{
    /// <summary>The shared namespace. Existing files stay here.</summary>
    public const string Common = "common";

    /// <summary>Host-specific folders. Only the folder matching the running host applies; the others are ignored.</summary>
    public static IReadOnlySet<string> ProviderFolders { get; } = new HashSet<string>(StringComparer.Ordinal) { "linux", "windows" };

    /// <summary>
    /// A valid name is snake_case: a lowercase letter, then lowercase letters, digits or underscores. MSBuild writes folder names into
    /// embedded resource names with <c>-</c> replaced by <c>_</c>, so the snake_case form is the only one that survives the round trip.
    /// </summary>
    public static bool IsValid(string name) => !string.IsNullOrEmpty(name) && ValidName().IsMatch(name);

    /// <summary>
    /// Orders namespaces for one run: <c>common</c> first, then the namespaces of components in <see cref="ComponentGraph.Order"/>
    /// order (dependencies first, ties by id), then every other namespace in ordinal name order. Deterministic on every start.
    /// </summary>
    /// <exception cref="MigrationException">A namespace name is invalid, or two components share a namespace.</exception>
    /// <exception cref="ComponentGraphException">The component graph cannot start.</exception>
    public static IReadOnlyList<string> Order(IEnumerable<string> namespaces, IEnumerable<ComponentManifest> components)
    {
        var remaining = new HashSet<string>(namespaces, StringComparer.Ordinal);
        foreach (var ns in remaining)
        {
            if (!IsValid(ns))
                throw new MigrationException($"Migration namespace '{ns}' is not a valid name; use lowercase letters, digits and underscores, starting with a letter.");
        }

        var ordered = new List<string>(remaining.Count);
        if (remaining.Remove(Common))
            ordered.Add(Common);

        var componentNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in ComponentGraph.Order(components))
        {
            var ns = manifest.MigrationNamespace;
            if (ns == Common || !componentNamespaces.Add(ns))
                throw new MigrationException($"Component '{manifest.Id}' maps to migration namespace '{ns}', which is already taken.");
            if (remaining.Remove(ns))
                ordered.Add(ns);
        }

        ordered.AddRange(remaining.Order(StringComparer.Ordinal));
        return ordered;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ValidName();
}
