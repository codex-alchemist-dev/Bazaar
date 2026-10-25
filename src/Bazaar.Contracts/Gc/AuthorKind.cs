using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gc;

/// <summary>Who wrote a GC event row (docs/design/01-gc-data-model.md, provenance).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthorKind>))]
public enum AuthorKind
{
    /// <summary>The owner.</summary>
    [JsonStringEnumMemberName("owner")]
    Owner,

    /// <summary>A human co-member.</summary>
    [JsonStringEnumMemberName("human")]
    Human,

    /// <summary>An agent.</summary>
    [JsonStringEnumMemberName("agent")]
    Agent,

    /// <summary>The server itself.</summary>
    [JsonStringEnumMemberName("system")]
    System,

    /// <summary>A bridge that relays external traffic (email, GitHub) into the GC.</summary>
    [JsonStringEnumMemberName("external_bridge")]
    ExternalBridge,
}
