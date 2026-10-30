namespace Bazaar.Server.Components;

/// <summary>
/// Declares one Bazaar Component: its identity, what it needs and what it provides (docs/design/BAZAAR-COMPONENTS.md section 2).
/// The kernel reads manifests before any component code runs, so a bad graph fails at start with the ids named.
/// </summary>
/// <param name="Id">Lowercase kebab-case, unique per install, stable across versions.</param>
/// <param name="Version">SemVer of the component.</param>
/// <param name="Kind">What the component is for.</param>
/// <param name="DependsOn">Component ids this one needs. Each must be present and enabled.</param>
/// <param name="Provides">Contract names this component implements, for example <c>gateway:adapter:openai_chat</c>.</param>
public sealed record ComponentManifest(
    string Id,
    string Version,
    ComponentKind Kind,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Provides)
{
    /// <summary>Migration namespace folder name. Defaults to the id, so each component owns its own SQL (section 5).</summary>
    public string MigrationNamespace => Id;
}
