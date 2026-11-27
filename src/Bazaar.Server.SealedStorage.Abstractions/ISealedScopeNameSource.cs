namespace Bazaar.Server.SealedStorage;

/// <summary>
/// Names the sealed scopes a module owns (for example <c>host:bootstrap</c>). A scope's folder on disk is named by a hash of its name and its
/// key header is bound to the name, so the KEK wrap check (the KEK wrap verifier in the SealedStorage module) can only test
/// scopes it is told about. A source must not depend on the KEK or the store: it is read while the KEK is still opening.
/// </summary>
public interface ISealedScopeNameSource
{
    /// <summary>The scope names this module may have created. Names that do not exist on disk are ignored.</summary>
    ValueTask<IReadOnlyCollection<string>> ListScopeNamesAsync(CancellationToken cancellationToken);
}
