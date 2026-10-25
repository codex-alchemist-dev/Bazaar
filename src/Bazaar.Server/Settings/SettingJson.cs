using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Settings;

/// <summary>Wire form of setting values: enums are snake_case names (<c>windows_service</c>), and integers never accept names.</summary>
internal static class SettingJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };
}
