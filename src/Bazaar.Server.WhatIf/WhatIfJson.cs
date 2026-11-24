using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.WhatIf;

/// <summary>Source-generated JSON for the jsonb columns of <c>what_if_run</c>.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WhatIfBranchResult))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WhatIfJson : JsonSerializerContext;
