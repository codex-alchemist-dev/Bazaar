namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// The only kinds of file the mounted folder may hold (docs/design/14-identity-service-and-passport.md, "Mounted folder").
/// </summary>
public enum MountedFileKind
{
    /// <summary>Service names, handles and expiry. Carries no secret.</summary>
    Metadata = 0,

    /// <summary>The session-cookie profile for an allowlisted account profile, materialised while a session is active.</summary>
    CookieProfile = 1,

    /// <summary>A decoy entry for a service in <c>identity.canary_services</c>. Any use is a detection event.</summary>
    Canary = 2,
}
