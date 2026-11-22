using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.WebAdmin;

namespace Bazaar.StatusTui;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StatusEnvelope))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;

/// <summary>The panel's read envelope: <c>{"available":true,"data":{...}}</c> (doc 51 section 5).</summary>
internal sealed record StatusEnvelope(bool Available, AdminStatusView? Data);

internal static class StatusJson
{
    /// <summary>Parses the body of <c>GET /admin/api/status</c>. Null when the section is unavailable or the body is not a status view.</summary>
    public static AdminStatusView? Parse(string body)
    {
        try
        {
            var env = JsonSerializer.Deserialize(body, StatusJsonContext.Default.StatusEnvelope);
            return env is { Available: true } ? env.Data : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
