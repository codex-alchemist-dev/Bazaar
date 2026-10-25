using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gateway;

/// <summary>
/// JSON options for gateway contracts. Enum values are snake_case strings, matching the names in docs/design/05-model-gateway.md.
/// Property casing and the final wire format are not decided yet (docs/design/DECISIONS-LOG.md, wire format OPEN).
/// </summary>
public static class GatewayJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
