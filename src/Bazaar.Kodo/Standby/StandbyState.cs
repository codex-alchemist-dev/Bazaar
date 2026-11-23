using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Standby;

/// <summary>
/// One node's standby state: its role and the epoch facts it must keep across a restart. Also the payload of every standby journal
/// entry, so the fencing epoch is in every record (doc 46 section 7.6).
/// </summary>
public sealed record StandbyState
{
    /// <summary>The only schema version this build writes and accepts.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    public required int Version { get; init; }

    /// <summary>This node's id (1 to 128 characters).</summary>
    public required string NodeId { get; init; }

    /// <summary>The role now.</summary>
    public required StandbyRole Role { get; init; }

    /// <summary>The epoch this node holds as primary, or the last one it held. 0 when it never held one.</summary>
    public required long Epoch { get; init; }

    /// <summary>The highest epoch this node has seen anywhere. Never below <see cref="Epoch"/>.</summary>
    public required long HighestEpochSeen { get; init; }

    /// <summary>The newest lease this node knows of, or null.</summary>
    public required LeaseRecord? Lease { get; init; }

    /// <summary>The cause code of the last change (see <see cref="StandbyCauses"/>), or null at the start.</summary>
    public required string? Cause { get; init; }

    /// <summary>When the state was recorded, in UTC.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>True when the last change was a split brain. Fenced nodes in this state need a human to resolve it.</summary>
    [JsonIgnore]
    public bool IsSplitBrain => Role == StandbyRole.Fenced && Cause == StandbyCauses.SplitBrain;

    /// <summary>True when every field is within its allowed range.</summary>
    [JsonIgnore]
    public bool IsWellFormed =>
        Version == CurrentVersion
        && !string.IsNullOrEmpty(NodeId) && NodeId.Length <= 128
        && Enum.IsDefined(Role)
        && Epoch >= 0
        && HighestEpochSeen >= Epoch
        && (Lease is null || (Lease.IsWellFormed && Lease.Epoch <= HighestEpochSeen))
        && (Cause is null || Cause.Length <= 64)
        && At.Offset == TimeSpan.Zero;
}
