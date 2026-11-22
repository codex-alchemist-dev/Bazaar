namespace Bazaar.Server.Security.Readers;

/// <summary>
/// The owner acting through an authenticated owner session. The only principal that may release a quarantined
/// hand-over. The auth layer creates it after verifying the owner; readers and actors have no way to obtain one,
/// because nothing in the reader path accepts a session id where this type is required.
/// </summary>
public sealed record OwnerPrincipal
{
    /// <summary>Creates the principal for a verified owner session.</summary>
    /// <exception cref="ArgumentException">The owner id is blank.</exception>
    public OwnerPrincipal(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("Owner id must not be blank.", nameof(ownerId));
        }

        OwnerId = ownerId;
    }

    /// <summary>The owner's stable id.</summary>
    public string OwnerId { get; }
}
