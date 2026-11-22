using System.Text.Json.Serialization;
using Bazaar.Contracts.Athanor;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Roadmap;

using Bazaar.Contracts.Ryusei;

namespace Bazaar.Contracts.Wire;

/// <summary>
/// Source-generated JSON metadata for every wire payload. Only the wire work package edits this file
/// (dev plan item W1); other areas must not add types to it.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(EditorOpenRequest))]
[JsonSerializable(typeof(EditorOpenReply))]
[JsonSerializable(typeof(EditorSaveRequest))]
[JsonSerializable(typeof(EditorSaveReply))]
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
[JsonSerializable(typeof(GcMemberAdd))]
[JsonSerializable(typeof(GcMemberRemove))]
[JsonSerializable(typeof(GcReact))]
[JsonSerializable(typeof(GcReactResult))]
[JsonSerializable(typeof(GcReactionsRequest))]
[JsonSerializable(typeof(GcReactionsReply))]
[JsonSerializable(typeof(WireGcReaction))]
[JsonSerializable(typeof(GcTyping))]
[JsonSerializable(typeof(GcTypingResult))]
[JsonSerializable(typeof(PresenceTyping))]
[JsonSerializable(typeof(PresenceUpdate))]
[JsonSerializable(typeof(PresenceSnapshotRequest))]
[JsonSerializable(typeof(PresenceSnapshot))]
[JsonSerializable(typeof(PaneFrame))]
[JsonSerializable(typeof(GcHistory))]
[JsonSerializable(typeof(GcPage))]
[JsonSerializable(typeof(GcRead))]
[JsonSerializable(typeof(PrefsSet))]
[JsonSerializable(typeof(PrefsSetResult))]
[JsonSerializable(typeof(GraphSnapshot))]
[JsonSerializable(typeof(GitSolarSnapshot))]
[JsonSerializable(typeof(GitSolarFeed))]
[JsonSerializable(typeof(KanbanBoardSnapshot))]
[JsonSerializable(typeof(KanbanBoardRequest))]
[JsonSerializable(typeof(KanbanMoveRequest))]
[JsonSerializable(typeof(KanbanMoveReply))]
[JsonSerializable(typeof(KanbanAssignRequest))]
[JsonSerializable(typeof(KanbanAssignReply))]
[JsonSerializable(typeof(RyuseiTriggerChangeRequest))]
[JsonSerializable(typeof(RyuseiTriggerChangeReply))]
[JsonSerializable(typeof(RyuseiTriggerListRequest))]
[JsonSerializable(typeof(RyuseiTriggerListReply))]
[JsonSerializable(typeof(HiveGrantChangeRequest))]
[JsonSerializable(typeof(HiveGrantChangeReply))]
[JsonSerializable(typeof(PrListRequest))]
[JsonSerializable(typeof(PrListReply))]
[JsonSerializable(typeof(PrSummaryRequest))]
[JsonSerializable(typeof(PrSummaryReply))]
[JsonSerializable(typeof(PrHunksRequest))]
[JsonSerializable(typeof(PrHunksReply))]
[JsonSerializable(typeof(PrReviewRequest))]
[JsonSerializable(typeof(PrReviewReply))]
[JsonSerializable(typeof(PrMergeRequest))]
[JsonSerializable(typeof(PrMergeReply))]
[JsonSerializable(typeof(RoadmapBoardSnapshot))]
[JsonSerializable(typeof(LayoutFrame))]
[JsonSerializable(typeof(FocusRequest))]
[JsonSerializable(typeof(GraphLodFrame))]
[JsonSerializable(typeof(TileKey))]
[JsonSerializable(typeof(ViewportRequest))]
[JsonSerializable(typeof(ClusterGlyph))]
[JsonSerializable(typeof(TileFrame))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(ReleaseArtifact))]
public sealed partial class WireJsonContext : JsonSerializerContext;
