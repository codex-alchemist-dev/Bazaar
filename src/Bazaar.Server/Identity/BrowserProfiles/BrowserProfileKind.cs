namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>Kinds of browser profile (doc 14, "Browser (C22)").</summary>
public enum BrowserProfileKind
{
    /// <summary>No credentials, used to read arbitrary pages. An anonymous profile never holds a cookie.</summary>
    Anonymous,

    /// <summary>Allowlisted sites only. Cookies come from the mounted profile or are injected by the broker.</summary>
    Account,
}
