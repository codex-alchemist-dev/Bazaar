using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Vms;

/// <summary>
/// Source-generated JSON metadata for guest protocol bodies. Field names are camelCase; unknown fields are
/// skipped so a later minor version can add fields.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(Exec))]
[JsonSerializable(typeof(Output))]
[JsonSerializable(typeof(Cancel))]
[JsonSerializable(typeof(Exit))]
[JsonSerializable(typeof(PutFile))]
[JsonSerializable(typeof(GetFile))]
[JsonSerializable(typeof(Heartbeat))]
[JsonSerializable(typeof(PtyOpen))]
[JsonSerializable(typeof(PtyData))]
[JsonSerializable(typeof(PtyResize))]
[JsonSerializable(typeof(PtyClose))]
public sealed partial class GuestJsonContext : JsonSerializerContext;
