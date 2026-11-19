using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gc;

/// <summary>What kind of participant a GC member is. Not the Core Principal type (agent, user, service, system).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GcParticipantKind>))]
public enum GcParticipantKind
{
    /// <summary>The owner, who reads everything.</summary>
    [JsonStringEnumMemberName("owner")]
    Owner,

    /// <summary>A human co-member.</summary>
    [JsonStringEnumMemberName("human")]
    Human,

    /// <summary>An agent.</summary>
    [JsonStringEnumMemberName("agent")]
    Agent,
}
