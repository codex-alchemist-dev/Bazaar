namespace Bazaar.Server.Security.Policy;

/// <summary>
/// The reader and actor split (doc 15, "Reader and actor sessions"). A reader holds untrusted input and no outward
/// tools; an actor holds credentials and outward tools and never reads external content.
/// </summary>
[Flags]
public enum SessionRoles
{
    /// <summary>No session role. Used by no tool; a tool with no roles is rejected at registration.</summary>
    None = 0,

    /// <summary>Reads untrusted input. Always hard-tainted.</summary>
    Reader = 1,

    /// <summary>Holds credentials and acts. Never reads external content.</summary>
    Actor = 2,
}
