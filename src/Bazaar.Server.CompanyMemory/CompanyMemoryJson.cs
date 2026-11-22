using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Knowledge.CompanyMemory;

namespace Bazaar.Server.CompanyMemory;

/// <summary>Source-generated JSON for the Company Memory routes (camelCase), so the module stays AOT-safe.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RecordDecisionBody))]
[JsonSerializable(typeof(RecordedDecision))]
[JsonSerializable(typeof(DecisionChainView))]
internal sealed partial class CompanyMemoryJson : JsonSerializerContext;
