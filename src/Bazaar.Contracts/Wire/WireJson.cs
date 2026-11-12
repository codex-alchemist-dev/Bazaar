using System.Text.Json.Serialization;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Roadmap;

namespace Bazaar.Contracts.Wire;

/// <summary>
/// Source-generated JSON metadata for every wire payload. Only the wire work package edits this file
/// (dev plan item W1); other areas must not add types to it.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(Welcome))]
[JsonSerializable(typeof(Subscribe))]
[JsonSerializable(typeof(Unsubscribe))]
[JsonSerializable(typeof(SubscribeResult))]
[JsonSerializable(typeof(Resync))]
[JsonSerializable(typeof(Lagged))]
[JsonSerializable(typeof(StreamRevoked))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
[JsonSerializable(typeof(WireError))]
[JsonSerializable(typeof(WireGcMessage))]
[JsonSerializable(typeof(GcSend))]
[JsonSerializable(typeof(GcSendResult))]
[JsonSerializable(typeof(GcEdit))]
[JsonSerializable(typeof(GcHistory))]
[JsonSerializable(typeof(GcPage))]
[JsonSerializable(typeof(GcRead))]
[JsonSerializable(typeof(GraphSnapshot))]
[JsonSerializable(typeof(KanbanBoardSnapshot))]
[JsonSerializable(typeof(KanbanBoardRequest))]
[JsonSerializable(typeof(RoadmapBoardSnapshot))]
[JsonSerializable(typeof(LayoutFrame))]
[JsonSerializable(typeof(FocusRequest))]
[JsonSerializable(typeof(GraphLodFrame))]
[JsonSerializable(typeof(TileKey))]
[JsonSerializable(typeof(ViewportRequest))]
[JsonSerializable(typeof(ClusterGlyph))]
[JsonSerializable(typeof(TileFrame))]
public sealed partial class WireJsonContext : JsonSerializerContext;
