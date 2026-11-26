using System.Text.Json.Serialization;

namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>Source-generated JSON for the drill's stored step list (AOT safe).</summary>
[JsonSerializable(typeof(List<DrillStepResult>))]
internal sealed partial class DrillJsonContext : JsonSerializerContext
{
}
