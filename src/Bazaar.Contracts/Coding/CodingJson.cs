using System.Text.Json;

namespace Bazaar.Contracts.Coding;

/// <summary>JSON options for coding-harness contracts. Enum values use the wire names in each enum's attributes.</summary>
public static class CodingJson
{
    /// <summary>Shared options for serializing coding-harness contracts.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
