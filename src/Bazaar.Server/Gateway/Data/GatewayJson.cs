using System.Text.Json;
using Bazaar.Server.Gateway.Policy;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Gateway.Data;

/// <summary>JSON helpers shared by the gateway repositories: string arrays stored as jsonb.</summary>
internal static class GatewayJson
{
    public static string Array(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToArray());

    public static List<string> ReadStrings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
    }

    /// <summary>Reads route members; an entry that is not a provider-qualified model is skipped, which can only shrink a route.</summary>
    public static List<ModelRef> ReadModelRefs(string json) =>
        [.. ReadStrings(json).Select(s => ModelRef.TryParse(s, out var m) ? (ModelRef?)m : null).Where(m => m is not null).Select(m => m!.Value)];

    public static NpgsqlParameter Jsonb(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };
}
