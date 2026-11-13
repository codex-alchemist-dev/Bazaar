namespace Bazaar.Server.Modules.Deps;

/// <summary>
/// Declares one bundled dependency as a module (docs/design/42-deps-as-modules.md sections 5 and 8). A dependency with several
/// version lines, such as PostgreSQL 18 and 19, is one manifest with one <see cref="Lines"/> entry per line (D2).
/// The manifest is data: it never declares a line's role as a fact about the package; the registry assigns roles (section 5.3).
/// </summary>
/// <param name="Id">Lowercase slug, unique per install and stable across versions, for example <c>postgresql</c>.</param>
/// <param name="DisplayName">Name shown in the module manager.</param>
/// <param name="Tags">Must include <see cref="RequiredTags"/> (D1). Shown in the module manager filter.</param>
/// <param name="Lines">The version lines this module ships. Line ids are digits, for example <c>18</c>.</param>
/// <param name="Extensions">Extension names loaded into this dependency, for example <c>pgvector</c>.</param>
/// <param name="LicenceSpdx">SPDX identifier of the licence, as recorded in the manifest. A claim alone is never enough (section 13.1).</param>
/// <param name="LicenceVerdict">Verdict from the licence check (D8). The validator takes it as input and never decides it.</param>
public sealed record DependencyManifest(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Tags,
    IReadOnlyList<DependencyLine> Lines,
    IReadOnlyList<string> Extensions,
    string LicenceSpdx,
    DependencyLicenceVerdict LicenceVerdict)
{
    /// <summary>Tags every bundled third-party dependency carries (section 8.4, rule 4). Both are fixed and case-sensitive.</summary>
    public static IReadOnlyList<string> RequiredTags { get; } = ["Deps", "3rd party"];
}

/// <summary>One version line of a dependency, for example PostgreSQL 19 at <c>19.0.0</c>.</summary>
/// <param name="Line">The line id, digits only, for example <c>19</c>.</param>
/// <param name="Version">Normalised three-part SemVer of the upstream release, for example <c>19.0.0</c> (section 5.2).</param>
/// <param name="Role">The line's role in the install's registry (section 5.3).</param>
public sealed record DependencyLine(string Line, string Version, DependencyLineRole Role);

/// <summary>The registry role of a version line (docs/design/42-deps-as-modules.md section 5.3).</summary>
public enum DependencyLineRole
{
    /// <summary>The latest GA line Bazaar supports. Exactly one per dependency.</summary>
    Newest,

    /// <summary>The previous major line, still supported and the fallback target. At most one per dependency.</summary>
    Stable,

    /// <summary>Upstream end of life or retired by policy. Read-only, and never the newest line.</summary>
    Retired,
}

/// <summary>The verdict of the AGPL-compatibility check for a dependency (docs/design/47-licensing-and-trademarks.md, D8).</summary>
public enum DependencyLicenceVerdict
{
    /// <summary>AGPL-compatible and verified. No issue is raised.</summary>
    Compatible,

    /// <summary>Not AGPL-compatible. An error with no override path.</summary>
    Incompatible,

    /// <summary>Compatible on the record but not yet verified. The dependency installs with a warning.</summary>
    Unverified,
}
