using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.Hive;

/// <summary>
/// Operator options for the copy flow. Defaults are the most protective reading of docs 08 and 25: the allowed
/// kinds are procedure and failure only (doc 25), the review window is the setting default of 30 days, and a
/// copy is never ranked above T2.
/// </summary>
public sealed record HiveCopyOptions
{
    private readonly int _reviewDays = 30;

    /// <summary><c>hive.grant_review_days</c>, 0 to 365. 0 keeps every batch under review until an owner approves it.</summary>
    public int ReviewDays
    {
        get => _reviewDays;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 365);
            _reviewDays = value;
        }
    }

    /// <summary><c>hive.allowed_kinds</c>. Doc 25 defaults to procedure and failure; doc 08 lists technical facts too.</summary>
    public IReadOnlySet<HiveKind> AllowedKinds { get; init; } =
        new HashSet<HiveKind> { HiveKind.Procedure, HiveKind.Failure };

    /// <summary>Agent id that owns the Vault commits of the copy flow.</summary>
    public string WriterAgentId { get; init; } = "hive";

    /// <summary>Highest tier a copy may carry in <c>memory.trust</c>. Copies never promote themselves, so the default is T2.</summary>
    public MemoryTier MaxCopyTier { get; init; } = MemoryTier.AgentVerified;
}
