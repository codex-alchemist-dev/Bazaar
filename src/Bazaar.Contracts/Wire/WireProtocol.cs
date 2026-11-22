namespace Bazaar.Contracts.Wire;

/// <summary>
/// The wire protocol version this build speaks (doc 02 versioning, doc 90 section 5). The schema generator reads these
/// constants into <c>schema.json</c>. A breaking change to a published payload needs <see cref="Major"/> raised
/// together with a regenerated <c>docs/wire/schema.lock.json</c>.
/// </summary>
public static class WireProtocol
{
    /// <summary>Protocol major version. Raised only by a breaking schema change.</summary>
    public const int Major = 1;

    /// <summary>Protocol minor version. Raised by additive changes that clients should be able to detect.</summary>
    public const int Minor = 0;
}
