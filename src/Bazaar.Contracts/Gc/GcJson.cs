using System.Text.Json;

namespace Bazaar.Contracts.Gc;

/// <summary>JSON options for GC contracts. Enum values use the wire names in each enum's attributes.</summary>
public static class GcJson
{
    /// <summary>Shared options for serializing GC contracts.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
