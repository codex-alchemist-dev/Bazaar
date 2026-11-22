namespace Bazaar.Server.SchemaIdentity;

/// <summary>
/// Supplies the server's schema identity (doc 04, "Schema identity"): the digest of the sorted <c>namespace:highest-migration</c>
/// pairs it has applied. The wire layer puts it in <c>welcome.schemaVersion</c>.
/// </summary>
public interface ISchemaIdentityProvider
{
    /// <summary>
    /// The identity string (<c>sha256:</c> plus lowercase hex), or null when the migration history cannot be read yet, which a client
    /// treats as unknown. Must not throw for an unreadable history.
    /// </summary>
    ValueTask<string?> GetAsync(CancellationToken ct = default);
}
