namespace Bazaar.Server.Modules.Trust;

/// <summary>Verifies a module package signature against the server trust store. Never throws for a bad package.</summary>
public interface IModuleSignatureVerifier
{
    /// <summary>Verifies the package. Runs on download, on install and on every start (doc 48 section 1).</summary>
    ModuleSignatureStatus Verify(ModulePackage package);
}
