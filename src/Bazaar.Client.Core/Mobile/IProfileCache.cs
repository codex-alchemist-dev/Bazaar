namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// Stores the last <see cref="PhoneProfile"/> so a cold start can paint the shell before the network answers (doc 29,
/// doc 27 "Offline cold start"). Implementations hold the profile only, never message bodies.
/// </summary>
public interface IProfileCache
{
    /// <summary>The stored profile, or null when there is none. A missing or corrupt store is null, never an exception.</summary>
    CachedProfile? Load();

    /// <summary>Replaces the stored profile and stamps the save time.</summary>
    void Save(PhoneProfile profile);
}
