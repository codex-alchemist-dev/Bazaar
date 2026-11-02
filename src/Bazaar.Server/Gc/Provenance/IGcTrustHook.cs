using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Gc.Provenance;

/// <summary>
/// The trust tagging hook on the GC write path (docs/design/01 "Provenance and trust", docs/design/15). The writer calls it
/// before the append transaction opens, so it never runs inside the transaction and never touches the database.
/// </summary>
public interface IGcTrustHook
{
    /// <summary>
    /// Returns the request with <see cref="AppendRequest.TaintedAtWrite"/> and <see cref="AppendRequest.TaintSourcesJson"/>
    /// filled in. A value the caller set explicitly is kept as it is; only unset fields are derived.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">An explicit <c>TaintSourcesJson</c> is not a JSON array.</exception>
    AppendRequest Resolve(AppendRequest request);
}
