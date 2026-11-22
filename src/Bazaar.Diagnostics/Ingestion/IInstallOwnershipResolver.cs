namespace Bazaar.Diagnostics.Ingestion;

/// <summary>
/// Answers whether an install id belongs to the device on the authenticated connection (doc 43, section 12.1 step 1).
/// The caller knows the connection and the device registry (doc 02, doc 32); the pipeline does not.
/// </summary>
public interface IInstallOwnershipResolver
{
    /// <summary>True when <paramref name="installId"/> is registered to the authenticated device.</summary>
    bool OwnsInstall(Guid installId);
}
