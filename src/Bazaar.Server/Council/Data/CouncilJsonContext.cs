using System.Text.Json.Serialization;

namespace Bazaar.Server.Council.Data;

/// <summary>Source-generated JSON for the two jsonb string arrays on <c>council_vote</c> (options, eligible_snapshot).</summary>
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CouncilJsonContext : JsonSerializerContext
{
}
