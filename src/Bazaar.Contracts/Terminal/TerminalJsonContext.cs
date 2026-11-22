using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Terminal;

/// <summary>
/// Source-generated JSON metadata for the terminal frames (doc 52, section 6). Field names are camelCase, nulls are
/// omitted, and unknown members are skipped so a later minor version can add fields. <see cref="TerminalFrameCodec"/>
/// adds the <c>type</c> discriminator around these bodies.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(TerminalOpen))]
[JsonSerializable(typeof(TerminalOpened))]
[JsonSerializable(typeof(TerminalOutput))]
[JsonSerializable(typeof(TerminalInput))]
[JsonSerializable(typeof(TerminalResize))]
[JsonSerializable(typeof(TerminalControl))]
[JsonSerializable(typeof(TerminalReplay))]
[JsonSerializable(typeof(TerminalExit))]
[JsonSerializable(typeof(TerminalClose))]
[JsonSerializable(typeof(TerminalError))]
public sealed partial class TerminalJsonContext : JsonSerializerContext;
