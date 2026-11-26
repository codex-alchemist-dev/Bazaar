namespace Bazaar.Server.CodingPermissions;

/// <summary>
/// What kind of side effect a tool can have (doc 15, "Enforcement"). Mirrors Server's <c>CapabilityClass</c> member for
/// member and in the same order, because modules cannot reference Server; one test in the tests project checks the match.
/// </summary>
public enum ToolCapabilityClass
{
    /// <summary>Reads data with no side effect outside the server.</summary>
    Read,

    /// <summary>Runs a command inside a workspace VM (doc 17).</summary>
    Exec,

    /// <summary>Publishes content where others can see it.</summary>
    Post,

    /// <summary>Sends a message to someone outside the install, such as mail.</summary>
    Send,

    /// <summary>Spends money or quota with a provider.</summary>
    Spend,

    /// <summary>Pushes code or content to a remote.</summary>
    Push,

    /// <summary>Merges a change into a shared branch.</summary>
    Merge,

    /// <summary>Creates an account on an external service.</summary>
    CreateAccount,

    /// <summary>Grants a new scope, permission or allowlist entry.</summary>
    GrantScope,

    /// <summary>Installs a package or dependency.</summary>
    InstallPackage,

    /// <summary>Deploys or releases something.</summary>
    Deploy,
}
