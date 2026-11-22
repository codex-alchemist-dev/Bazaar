namespace Bazaar.Server.CodingPermissions;

/// <summary>Taint of a session (doc 15). Ordered so the larger value is the more restrictive one.</summary>
public enum SessionTaint
{
    /// <summary>No untrusted input seen.</summary>
    None = 0,

    /// <summary>Agent text seen. Routine work continues.</summary>
    Soft = 1,

    /// <summary>External content seen (strangers' issues, web, email, third-party docs).</summary>
    Hard = 2,
}
