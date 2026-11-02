namespace Bazaar.Server.Components.Tls;

/// <summary>Where the TLS server certificate comes from (docs/design/32 section 2). Wire form is the snake_case name.</summary>
public enum TlsSourceKind
{
    /// <summary>Operator-supplied PEM certificate and key, or a PFX file. The default.</summary>
    Pem,

    /// <summary>ACME for an operator-owned domain. Not available in this build.</summary>
    Acme,

    /// <summary>A certificate issued by the overlay network. Not available in this build (unverified in the design).</summary>
    Overlay,
}
