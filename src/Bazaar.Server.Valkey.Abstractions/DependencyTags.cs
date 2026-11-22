namespace Bazaar.Server.Valkey;

/// <summary>
/// The dependency tags of docs/design/42-deps-as-modules.md section 8.1. This is the one source for the strings in Bazaar.Valkey;
/// the Bazaar.Server dependency validator (<c>DependencyManifest</c>) uses the same values, and that project is not referenced here.
/// TODO: when the planned <c>ComponentManifest</c> Tags field lands (doc 40 section 4c.4 item 7, module-system gate #769), the manifest
/// carries these values and this class stays the single source of the strings.
/// </summary>
public static class DependencyTags
{
    /// <summary>Role tag every dependency carries (doc 42 section 3). Case-sensitive.</summary>
    public const string Deps = "Deps";

    /// <summary>Origin tag a third-party dependency carries (doc 42 section 8.4, rule 4). Case-sensitive.</summary>
    public const string ThirdParty = "3rd party";
}
