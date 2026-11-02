using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// JSON options for engine adapter contracts. Built on the web defaults like <see cref="Gateway.GatewayJson"/>; enums are
/// snake_case strings (for example <c>hot_reload</c>, <c>script_check</c>). Null members are omitted, so a JSON-RPC
/// success response never carries <c>"error":null</c>.
/// </summary>
public static class EngineJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter<EngineCapability>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<RiskClass>(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
