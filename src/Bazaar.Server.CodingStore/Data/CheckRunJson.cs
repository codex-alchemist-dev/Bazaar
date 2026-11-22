using System.Text.Json.Serialization;

namespace Bazaar.Server.CodingStore.Data;

/// <summary>Source-generated JSON for the <c>tool_exit_codes</c> column, so the module stays AOT compatible.</summary>
[JsonSerializable(typeof(Dictionary<string, int?>))]
internal sealed partial class CheckRunJson : JsonSerializerContext;
