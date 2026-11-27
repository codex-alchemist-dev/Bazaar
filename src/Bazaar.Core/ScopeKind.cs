namespace Bazaar.Core;

/// <summary>The three scope kinds, from widest to narrowest.</summary>
public enum ScopeKind
{
    /// <summary>One installation as a whole.</summary>
    Install,

    /// <summary>A tenant within an installation. May carry a sub-path for sub-units.</summary>
    Tenant,

    /// <summary>One user's own data.</summary>
    User,
}
