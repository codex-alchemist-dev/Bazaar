using System.Text.Json.Serialization;

namespace Bazaar.Server.Council.Membership;

/// <summary>The final outcome of a membership motion as handed to the owning docs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MembershipDecision>))]
public enum MembershipDecision
{
    /// <summary>Passed by the Council (ordinary majority, or a unanimous bigger change).</summary>
    [JsonStringEnumMemberName("approved")]
    Approved,

    /// <summary>Failed by the Council.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>Escalated, then approved by the owner.</summary>
    [JsonStringEnumMemberName("owner_approved")]
    OwnerApproved,

    /// <summary>Escalated, then rejected by the owner.</summary>
    [JsonStringEnumMemberName("owner_rejected")]
    OwnerRejected,

    /// <summary>Withdrawn by the proposer or the owner before a final choice.</summary>
    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn,
}
