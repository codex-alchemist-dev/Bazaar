namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// One cookie in a browser profile. <see cref="Expires"/> is null for a session cookie. The value is never written to
/// logs: <see cref="ToString"/> prints only the name, domain and path.
/// </summary>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, DateTimeOffset? Expires)
{
    /// <inheritdoc />
    public override string ToString() => $"{Name}@{Domain}{Path}";
}
