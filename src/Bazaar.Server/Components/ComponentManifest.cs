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
/// <param name="SoftDependsOn">
/// Component ids this one uses when present. A missing soft dependency is not an error: the component runs without that
/// feature. When the dependency is enabled it starts first. Null means none. Default is hard (<see cref="DependsOn"/>).
/// </param>
public sealed record ComponentManifest(
    string Id,
    string Version,
    ComponentKind Kind,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string>? SoftDependsOn = null)
{
    /// <summary>The soft dependencies, never null.</summary>
    public IReadOnlyList<string> Soft => SoftDependsOn ?? [];

    /// <summary>
    /// Migration namespace folder name (section 5): the id with <c>-</c> replaced by <c>_</c>, so each component owns its own SQL
    /// under a name that survives the embedded-resource naming. Ids are kebab-case and cannot contain <c>_</c>, so this is one-to-one.
    /// </summary>
    public string MigrationNamespace => Id.Replace('-', '_');
}
