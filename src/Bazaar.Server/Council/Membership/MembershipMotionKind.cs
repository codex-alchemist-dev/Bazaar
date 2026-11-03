using System.Text.Json.Serialization;

namespace Bazaar.Server.Council.Membership;

/// <summary>The kind of membership or role motion a Council vote decided (docs/design/10-council-and-voting.md, Membership and role votes).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MembershipMotionKind>))]
public enum MembershipMotionKind
{
    /// <summary>Hire a candidate from the Talent Pool (doc 25).</summary>
    [JsonStringEnumMemberName("hire")]
    Hire,

    /// <summary>Promote an agent (doc 07).</summary>
    [JsonStringEnumMemberName("promote")]
    Promote,

    /// <summary>Retire an agent (doc 07).</summary>
    [JsonStringEnumMemberName("retire")]
    Retire,

    /// <summary>Any other role change.</summary>
    [JsonStringEnumMemberName("role_change")]
    RoleChange,

    /// <summary>A proposed change to the Company Constitution (doc 24).</summary>
    [JsonStringEnumMemberName("constitution_change")]
    ConstitutionChange,
}
