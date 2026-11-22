using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Standby;

/// <summary>The role a node holds in a warm standby pair (doc 46 section 7.6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StandbyRole>))]
public enum StandbyRole
{
    /// <summary>Holds the lease and may write. At most one node is in this role for any epoch.</summary>
    Primary,

    /// <summary>Warm copy that watches the primary and may take the lease when the rules allow.</summary>
    Standby,

    /// <summary>Gave up or lost the lease. Rejects every write until a human or the rejoin path returns it to standby.</summary>
    Fenced,
}

/// <summary>Stable cause codes for a state change. Short codes, never free text.</summary>
public static class StandbyCauses
{
    /// <summary>The primary's own lease ran out before it could renew.</summary>
    public const string LeaseLost = "lease_lost";

    /// <summary>A newer epoch exists, so this node is a stale primary.</summary>
    public const string Superseded = "superseded";

    /// <summary>Two nodes claim the same epoch. The top incident: both nodes stay fenced until a human resolves it.</summary>
    public const string SplitBrain = "split_brain";

    /// <summary>The primary has no lease record that matches its epoch.</summary>
    public const string LeaseMissing = "lease_missing";

    /// <summary>A restarted primary found its lease already expired.</summary>
    public const string RestartLeaseLost = "restart_lease_lost";

    /// <summary>The saved state could not be read or did not parse, so the node fences itself.</summary>
    public const string StateUnreadable = "state_unreadable";

    /// <summary>The standby took the lease.</summary>
    public const string Promoted = "promoted";

    /// <summary>A person returned a fenced node to standby.</summary>
    public const string ManualRejoin = "manual_rejoin";

    /// <summary>The first primary took its first lease.</summary>
    public const string Bootstrap = "bootstrap";
}
