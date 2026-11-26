using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Standby;

/// <summary>
/// A lease on the primary role (doc 46 section 7.6): who holds it, at which epoch, and until when. A pure record. Nothing in it is
/// signed; the witnesses are the authority and the digest (see <see cref="StandbyCodec"/>) only names this exact content.
/// </summary>
public sealed record LeaseRecord
{
    /// <summary>The only schema version this build writes and accepts.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    public required int Version { get; init; }

    /// <summary>The node that holds the lease (1 to 128 characters).</summary>
    public required string HolderId { get; init; }

    /// <summary>The fencing epoch of this lease (at least 1).</summary>
    public required long Epoch { get; init; }

    /// <summary>When the lease was granted or last renewed, in UTC.</summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>When the lease ends, in UTC. Must be after <see cref="GrantedAt"/>.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The fencing token of this lease.</summary>
    [JsonIgnore]
    public FencingToken Token => new(Epoch);

    /// <summary>True when every field is within its allowed range.</summary>
    [JsonIgnore]
    public bool IsWellFormed =>
        Version == CurrentVersion
        && !string.IsNullOrEmpty(HolderId) && HolderId.Length <= 128
        && Epoch >= 1
        && GrantedAt.Offset == TimeSpan.Zero
        && ExpiresAt.Offset == TimeSpan.Zero
        && ExpiresAt > GrantedAt;

    /// <summary>
    /// True when <paramref name="now"/> is at or after the expiry plus <paramref name="margin"/>. The primary checks with no margin and
    /// the standby with the clock skew margin, so the primary always stops before the standby may start.
    /// </summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan margin = default) => now >= ExpiresAt + margin;

    /// <summary>Builds a lease starting at <paramref name="grantedAt"/> and lasting <paramref name="duration"/>. Times are normalised to UTC.</summary>
    /// <exception cref="ArgumentException">The holder id, epoch or duration is out of range.</exception>
    public static LeaseRecord Create(string holderId, FencingToken token, DateTimeOffset grantedAt, TimeSpan duration)
    {
        LeaseRecord record = new()
        {
            Version = CurrentVersion,
            HolderId = holderId,
            Epoch = token.Epoch,
            GrantedAt = grantedAt.ToUniversalTime(),
            ExpiresAt = grantedAt.ToUniversalTime() + duration,
        };
        return record.IsWellFormed ? record : throw new ArgumentException("Holder id must be 1 to 128 characters, the epoch at least 1 and the duration positive.");
    }

    /// <summary>The lease that wins when two are compared: the higher epoch, then the later expiry. Null when both are null.</summary>
    public static LeaseRecord? Newer(LeaseRecord? first, LeaseRecord? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        if (second.Epoch != first.Epoch)
        {
            return second.Epoch > first.Epoch ? second : first;
        }

        return second.ExpiresAt > first.ExpiresAt ? second : first;
    }
}
