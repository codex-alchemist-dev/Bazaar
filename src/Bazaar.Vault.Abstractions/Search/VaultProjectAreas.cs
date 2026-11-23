namespace Bazaar.Server.Vault.Search;

/// <summary>
/// Which Vault paths are project areas, and of which project (doc 18, Repository table). The one source for Vault search,
/// link resolution and the Memory path rules, so none of them restates the roots.
/// </summary>
public static class VaultProjectAreas
{
    /// <summary>
    /// The roots whose second segment names the project an area belongs to: <c>projects/&lt;slug&gt;/**</c> and the Digital
    /// Twin's <c>twin/&lt;project&gt;/**</c> (doc 25). No other area is project-scoped by path.
    /// </summary>
    public static IReadOnlyList<string> Roots { get; } = ["projects/", "twin/"];

    /// <summary>
    /// The project slug of a project-scoped path: the second segment of a path under one of <see cref="Roots"/>, when the
    /// path has a non-empty slug and something after it. Null for every other path. Matching is ordinal.
    /// </summary>
    public static string? ProjectAreaOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (var root in Roots)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = path.AsSpan(root.Length);
            var slash = rest.IndexOf('/');
            return slash > 0 && slash < rest.Length - 1 ? rest[..slash].ToString() : null;
        }

        return null;
    }
}
