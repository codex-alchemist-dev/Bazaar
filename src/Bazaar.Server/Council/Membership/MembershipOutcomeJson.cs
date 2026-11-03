using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Council.Membership;

/// <summary>
/// Source-generated JSON metadata for the membership handoff. Only this file's context serialises it; no reflection-based
/// serializer is used.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(MembershipOutcome))]
public sealed partial class MembershipOutcomeJsonContext : JsonSerializerContext;

/// <summary>Serialises the membership handoff with <see cref="MembershipOutcomeJsonContext"/>.</summary>
public static class MembershipOutcomeJson
{
    /// <summary>Serialises an outcome to JSON.</summary>
    public static string Serialize(MembershipOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return JsonSerializer.Serialize(outcome, MembershipOutcomeJsonContext.Default.MembershipOutcome);
    }

    /// <summary>Deserialises an outcome from JSON. Returns null for a JSON null.</summary>
    public static MembershipOutcome? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, MembershipOutcomeJsonContext.Default.MembershipOutcome);
    }
}
