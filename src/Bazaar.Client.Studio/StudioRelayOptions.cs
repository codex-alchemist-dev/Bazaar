namespace Bazaar.Client.Studio;

/// <summary>Limits for <see cref="StudioRelay"/>. Defaults are conservative; the server's engine timeout (doc 20) bounds a whole command separately.</summary>
public sealed record StudioRelayOptions
{
    /// <summary>Per-call timeout for the forwarded tool call.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout for the grant check and for the Studio listing; a verifier that does not answer in time denies the call.</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest result payload returned, in UTF-8 bytes of its JSON; larger results are replaced by a truncated preview.</summary>
    public int MaxPayloadBytes { get; init; } = 256 * 1024;
}
