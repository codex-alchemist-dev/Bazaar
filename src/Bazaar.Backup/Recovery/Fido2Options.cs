namespace Bazaar.Backup.Recovery;

/// <summary>
/// Relying-party settings for hardware-key assertions (doc 48 section 7). There are no defaults: the operator supplies
/// the relying-party id and the exact origins, so nothing points at a real host by accident.
/// </summary>
public sealed record Fido2Options
{
    /// <summary>Validates and creates the options.</summary>
    /// <exception cref="ArgumentException">The id or an origin is empty or malformed, or no origin is given.</exception>
    public Fido2Options(string relyingPartyId, IEnumerable<string> allowedOrigins, bool allowRs256 = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingPartyId);
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        if (relyingPartyId.Any(char.IsWhiteSpace) || relyingPartyId.Contains('/', StringComparison.Ordinal) || relyingPartyId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The relying-party id must be a bare host name.", nameof(relyingPartyId));
        }

        var origins = new List<string>();
        foreach (var o in allowedOrigins)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(o);
            if (!Uri.TryCreate(o, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || uri.PathAndQuery != "/"
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || o.EndsWith('/'))
            {
                throw new ArgumentException("Each origin must be an https origin with no path, query or credentials.", nameof(allowedOrigins));
            }

            origins.Add(o);
        }

        if (origins.Count == 0)
        {
            throw new ArgumentException("At least one allowed origin is required.", nameof(allowedOrigins));
        }

        RelyingPartyId = relyingPartyId;
        AllowedOrigins = origins.Distinct(StringComparer.Ordinal).ToArray();
        AllowRs256 = allowRs256;
    }

    /// <summary>The WebAuthn relying-party id (a bare host name).</summary>
    public string RelyingPartyId { get; }

    /// <summary>Exact origins accepted in clientDataJSON.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; }

    /// <summary>Whether RS256 keys are accepted in addition to ES256. Off by default.</summary>
    public bool AllowRs256 { get; }
}
