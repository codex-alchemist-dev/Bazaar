using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Runtime;

/// <summary>
/// Source-generated JSON for the runtime manifest and slot pointer. Properties are written in declaration order, with no
/// indentation and no optional members, so one value has one encoding. Unknown members are rejected, so the bytes that
/// parse are exactly the content that was signed.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RuntimeManifest))]
[JsonSerializable(typeof(RuntimeFileEntry))]
[JsonSerializable(typeof(SignedRuntimeManifest))]
[JsonSerializable(typeof(SlotPointerBody))]
[JsonSerializable(typeof(SlotPointer))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext
{
}
