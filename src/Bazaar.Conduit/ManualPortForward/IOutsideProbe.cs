namespace Bazaar.Conduit;

/// <summary>
/// Checks from outside the host that a public address reaches this server. The probe fetches a URL served from the
/// outside network, which must echo the server key hash. Doc 39 section 2.
/// </summary>
public interface IOutsideProbe
{
    /// <summary>Returns true only when the probe answers successfully and the body contains <paramref name="expectedKeyHash"/>.</summary>
    Task<bool> ProbeAsync(Uri probe, string expectedKeyHash, CancellationToken cancellationToken);
}
