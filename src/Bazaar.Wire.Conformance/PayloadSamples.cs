using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Roadmap;
using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Xunit;

namespace Bazaar.Wire.Conformance;

/// <summary>One sample payload value, with the type-specific calls the conformance suite needs and cannot make generically.</summary>
public sealed class PayloadSample
{
    internal PayloadSample(
        string name,
        Type type,
        Func<IWireCodec, ReadOnlyMemory<byte>> encode,
        Func<IWireCodec, Envelope, object> decode,
        Func<ReadOnlyMemory<byte>> canonicalJson)
    {
        Name = name;
        Type = type;
        Encode = encode;
        Decode = decode;
        CanonicalJson = canonicalJson;
    }

    /// <summary>Unique name, the type name plus a variant when one type has several samples.</summary>
    public string Name { get; }

    /// <summary>The payload record type.</summary>
    public Type Type { get; }

    /// <summary>Encodes the sample with a codec.</summary>
    public Func<IWireCodec, ReadOnlyMemory<byte>> Encode { get; }

    /// <summary>Decodes an envelope's payload as the sample's type.</summary>
    public Func<IWireCodec, Envelope, object> Decode { get; }

    /// <summary>The sample as JSON. Used as the codec-neutral equality, since the JSON codec covers every payload.</summary>
    public Func<ReadOnlyMemory<byte>> CanonicalJson { get; }

    /// <summary>Message type the sample travels under in the envelope, or a neutral one when the payload has no type of its own.</summary>
    public string MessageType { get; internal init; } = "sample";

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Sample values for every <c>[WirePayload]</c> record. The suite fails when a payload has no sample, so a new payload cannot skip the conformance run.</summary>
public static class PayloadSamples
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly Guid _other = Guid.Parse("0196a6f0-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset _at = new(2026, 11, 19, 5, 36, 12, 345, TimeSpan.Zero);
    private static readonly DateTimeOffset _local = new(2026, 11, 19, 7, 36, 12, TimeSpan.FromHours(2));
    private static readonly string _text = "h\u00e9llo \U0001F642 \"quoted\" \\ \u2028 end";

    private static readonly WirePresenceRow _presenceRow = new(_author, 4, "OnShift", ["Throttled", "Idle"], "thinking", "ModelCall", _gc, _other, "planning the release", _at, _at);

    private static readonly WireGcMessage _message = new(_gc, 12, 40, _other, _author, _text, _at, true, [_author, _other], 7);

    /// <summary>Every sample, in a stable order.</summary>
    public static IReadOnlyList<PayloadSample> All { get; } = Build();

    /// <summary>Sample names, for <c>[MemberData]</c>.</summary>
    public static TheoryData<string> Names { get; } = [.. All.Select(s => s.Name)];

    /// <summary>Finds a sample by name.</summary>
    public static PayloadSample Get(string name) => All.First(s => s.Name == name);

    private static PayloadSample Of<T>(string? variant, T value, string messageType = "sample") where T : notnull =>
        new(
            variant is null ? typeof(T).Name : $"{typeof(T).Name}({variant})",
            typeof(T),
            codec => codec.EncodePayload(value),
            (codec, envelope) => codec.DecodePayload<T>(envelope),
            () => new JsonWireCodec().EncodePayload(value))
        {
            MessageType = messageType,
        };

    private static List<PayloadSample> Build() =>
    [
        Of(null, new Hello(1, 2, 3, ["cbor", "json"], "device-1", "desktop", "0.1.0+abc1234", "credential", [new StreamCursor("gc:" + _gc, 10, 2)]), WireTypes.Hello),
        Of("minimal", new Hello(1, 1, 0, ["json"], "d", "c", "b", null, null), WireTypes.Hello),
        Of(null, new Welcome(1, 0, "cbor", _author, _at, 25, 60, new WireLimits(1_048_576, 32, 200), ["codecs:json,cbor", "transports:ws"], 7, true, "0.1.0"), WireTypes.Welcome),
        Of("minimal", new Welcome(1, 0, "json", _author, _local, 25, 60, new WireLimits(1, 2, 3), [], 0, null, null), WireTypes.Welcome),
        Of(null, new Subscribe([new StreamCursor("gc:" + _gc, long.MaxValue, 0), new StreamCursor("ui", 0, long.MinValue + 1)]), WireTypes.Subscribe),
        Of(null, new Unsubscribe(["gc:" + _gc, "presence"]), WireTypes.Unsubscribe),
        Of(null, new PaneFrame("{\"t\":\"output\",\"terminalId\":\"t1\",\"seq\":1,\"bytes\":\"aGk=\"}"), WireTypes.PaneFrame),
        Of(null, new SubscribeResult([new StreamResult("gc:" + _gc, "ok"), new StreamResult("ui", "denied")])),
        Of(null, new Resync("gc:" + _gc, 99, 120, 3, [_message, _message with { Edited = false, ReplyToN = null, Mentions = [] }]), WireTypes.Resync),
        Of(null, new Lagged("gc:" + _gc, 41), WireTypes.Lagged),
        Of(null, new StreamRevoked("gc:" + _gc), WireTypes.StreamRevoked),
        Of(null, new Ping("nonce-1"), WireTypes.Ping),
        Of("no nonce", new Ping(null), WireTypes.Ping),
        Of(null, new Pong("nonce-1"), WireTypes.Pong),
        Of(null, new WireError(WireErrorCodes.RateLimited, "slow down", 1500)),
        Of("minimal", new WireError("unsupported", null, null)),
        Of(null, _message, WireTypes.GcMessage),
        Of(null, new GcSend(_gc, _text, [_author], 5), WireTypes.GcSend),
        Of("minimal", new GcSend(_gc, "", null, null), WireTypes.GcSend),
        Of(null, new GcSendResult(long.MaxValue, 0)),
        Of(null, new GcEdit(_gc, _other, _text), WireTypes.GcEdit),
        Of(null, new GcMemberAdd(_gc, _author, PrincipalKind.Agent, "member"), WireTypes.GcMemberAdd),
        Of("owner", new GcMemberAdd(_gc, _author, PrincipalKind.Owner, "admin"), WireTypes.GcMemberAdd),
        Of(null, new GcMemberRemove(_gc, _author), WireTypes.GcMemberRemove),
        Of(null, new GcHistory(_gc, 100, 50), WireTypes.GcHistory),
        Of(null, new GcPage([_message], true)),
        Of("empty", new GcPage([], false)),
        Of(null, new GcRead(_gc, 12), WireTypes.GcRead),
        Of(null, new GraphSnapshot(3, [new GraphNode("a", GraphNodeKind.Objective), new GraphNode("b")], [new GraphEdge("a", "b", GraphEdgeKind.DependsOn), new GraphEdge("b", "a")])),
        Of(null, new KanbanBoardSnapshot(_gc, [new KanbanBoardCard(_author, "Build it", "ready", _other, [_gc], ["Other"]), new KanbanBoardCard(_other, "Idle", "backlog", null, [], [])])),
        Of(null, new KanbanBoardRequest(_gc), WireTypes.BoardSnapshot),
        Of(null, new EngineRelayRequestFrame(_gc, DateTimeOffset.Parse("2026-11-18T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), new EngineRelayCallFrame("grant-1", "studio-1", "multi_edit", "{\"x\":1}")), EngineRelayWire.EventType),
        Of(null, new EngineRelayResultFrame(_gc, new EngineRelayCallResultFrame("grant-1", true, "{\"done\":true}")), EngineRelayWire.RequestType),
        Of(null, new PrListRequest("codex-alchemist-dev/Bazaar", PrListState.Open, null, 25), WireTypes.PrList),
        Of(null, new PrListReply([new PrListRow("PR_1", "codex-alchemist-dev/Bazaar", 7, "Wake the agent", "claude", PrAuthorKind.Agent, false, new string('a', 40), PrChecksRollup.Passing, 3)], null)),
        Of(null, new PrSummaryRequest("PR_1"), WireTypes.PrSummary),
        Of(null, new PrSummaryReply("PR_1", new string('a', 40), "Summary", [new PrChangedFile("src/a.cs", "modified", 3, 1)], PrChecksRollup.Passing, true, null)),
        Of(null, new PrHunksRequest("PR_1", new string('a', 40), "src/a.cs", null), WireTypes.PrHunks),
        Of(null, new PrHunksReply(PrHunksStatus.Ok, new string('a', 40), "src/a.cs", [new PrHunk("@@ -1 +1 @@", 1, 1, 1, 1, ["-a", "+b"])], null)),
        Of(null, new PrReviewRequest("PR_1", new string('a', 40), PrReviewEvents.Comment, "Looks fine"), WireTypes.PrReview),
        Of(null, new PrReviewReply(PrReviewStatus.Applied, new string('a', 40), null)),
        Of(null, new PrMergeRequest("PR_1", new string('a', 40), "Title", "Message", new PrOwnerPayload("pr.merge", _author, [1, 2, 3], new string('a', 40), _other, "device-1", new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 22, 8, 5, 0, TimeSpan.Zero), [4, 5, 6]), [7, 8, 9]), WireTypes.PrMerge),
        Of(null, new PrMergeReply(PrMergeStatus.Merged, new string('a', 40), null)),
        Of(null, new KanbanAssignRequest(_gc, _author, _other, 3), WireTypes.BoardAssign),
        Of(null, new KanbanAssignReply(KanbanAssignStatuses.Applied, 4)),
        Of(null, new ReleaseManifest("Stable", "0.1.0", "1.0", "1.2", "7", "0123456789abcdef0123456789abcdef01234567",
            [new ReleaseArtifact("linux", "linux-x64", "tar.gz", "https://releases.example.org/v0.1.0/Bazaar-0.1.0-linux-x64.tar.gz", 1024,
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "Bazaar-0.1.0-linux-x64.tar.gz.sha256", "Bazaar-0.1.0-linux-x64.tar.gz.asc")])),
        Of(null, new PresenceTyping(PresenceKinds.Typing, _gc, _author, true, 5000), WireTypes.PresenceUpdate),
        Of(null, new PresenceUpdate(PresenceKinds.Agent, _author, 4, _presenceRow), WireTypes.PresenceUpdate),
        Of(null, new PresenceSnapshotRequest(), WireTypes.PresenceSnapshot),
        Of(null, new PresenceSnapshot([_presenceRow, _presenceRow with { AgentId = _other, Revision = 1, Overlays = [], GcId = null, TurnId = null, Label = null }])),
        Of(null, new RyuseiTriggerChangeRequest(_gc), WireTypes.RyuseiPause),
        Of(null, new RyuseiTriggerChangeReply(_gc, 4)),
        Of(null, new RyuseiTriggerListRequest(_gc), WireTypes.RyuseiList),
        Of(null, new RyuseiTriggerListReply(_gc, [new RyuseiTriggerChip(_author, _other, "cron", "active", _at, "nightly", 3), new RyuseiTriggerChip(_other, null, "once", "paused", null, "", 1)])),
        Of(null, new RoadmapBoardSnapshot(
            _gc,
            [new RoadmapPhaseDto("p1", "roadmap/p1.md", "Phase 1", true, 2, 1, [Objective("o1", 0.75m)])],
            [Objective("o2", null)],
            ["a", "b"],
            [new RoadmapDocumentErrorDto("x.md", "owner", "missing")])),
        Of(null, new LayoutFrame(2, 9, "focus", new float[] { 0f, 1.5f, -2.25f, 1e-6f, float.MaxValue, 3f })),
        Of("empty", new LayoutFrame(2, 9, "", ReadOnlyMemory<float>.Empty)),
        Of(null, new FocusRequest("view-1", "node-1")),
        Of(null, new GraphLodFrame("graph:" + _gc, 8, false, [new GraphLodGlyph(1, [0.5f, 1.5f, 2.5f], 12)], [new GraphLodEdge(0, 1, 3)], [new GraphLodTile(0, 1, [1, 2, 3])]), WireTypes.GraphLod),
        Of(null, new TileKey(2, -3, 4, 5)),
        Of(null, new ViewportRequest("view-1", 3, ZoomTier.Mid, -1f, -2f, -3f, 1f, 2f, 3f, 500)),
        Of(null, new ClusterGlyph("c1", 40, 1f, 2f, 3f)),
        Of(null, new TileFrame("view-1", 3, 4, new TileKey(1, 0, 0), ZoomTier.Near, 3, ["a", "b"], new float[] { 1, 2, 3, 4, 5, 6 }, [new ClusterGlyph("c1", 40, 1f, 2f, 3f)])),
    ];

    private static RoadmapObjectiveDto Objective(string name, decimal? share) =>
        new($"roadmap/{name}.md", name, "p1", "open", name == "o1" ? "agent-1" : null, ["o0"], new RoadmapProgressDto(4, 1, 1, 1, 0, 1, share));
}
