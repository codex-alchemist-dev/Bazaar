using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gc;

/// <summary>What kind of principal a GC member is. Humans and agents are both principals.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrincipalKind>))]
public enum PrincipalKind
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
