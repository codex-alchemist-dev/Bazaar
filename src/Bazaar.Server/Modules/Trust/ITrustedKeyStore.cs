namespace Bazaar.Server.Modules.Trust;

/// <summary>Maps a publisher key id to its trusted public key. Lookups return null for unknown ids; they never throw.</summary>
public interface ITrustedKeyStore
{
    /// <summary>Returns the key for the id, or null when no key with that id is trusted.</summary>
    TrustedModuleKey? Find(string keyId);
}
