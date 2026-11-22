namespace Bazaar.Contracts.Wire;

/// <summary>Message types (<c>t</c>), the v1 sketch in docs/design/02-wire-protocol-and-sync.md.</summary>
public static class WireTypes
{
    /// <summary>Client opening message: versions, codecs, device, cursors.</summary>
    public const string Hello = "hello";

    /// <summary>Server answer to <see cref="Hello"/>.</summary>
    public const string Welcome = "welcome";

    /// <summary>Subscribe to streams with cursors.</summary>
    public const string Subscribe = "subscribe";

    /// <summary>Drop streams.</summary>
    public const string Unsubscribe = "unsubscribe";

    /// <summary>A GC message or message version, pushed by the server.</summary>
    public const string GcMessage = "gc.message";

    /// <summary>Client posts a message.</summary>
    public const string GcSend = "gc.send";

    /// <summary>Client edits its own message, adding a version. Answered with a <c>GcSendResult</c>: the edit event's seq and the new version.</summary>
    public const string GcEdit = "gc.edit";

    /// <summary>Client invites a principal into a GC. Answered with a <c>GcSendResult</c>: the membership event's seq.</summary>
    public const string GcMemberAdd = "gc.member.add";

    /// <summary>Client removes a principal, or leaves a GC itself. Answered with a <c>GcSendResult</c>: the membership event's seq.</summary>
    public const string GcMemberRemove = "gc.member.remove";

    /// <summary>Client adds or removes an emoji reaction on a message (doc 02, "Reactions on the wire"). Answered with a <c>GcReactResult</c>.</summary>
    public const string GcReact = "gc.react";

    /// <summary>Client asks for the folded reaction state of a GC. Answered with a <c>GcReactionsReply</c>.</summary>
    public const string GcReactions = "gc.reactions";

    /// <summary>Server event: one reaction was added or removed (<c>WireGcReaction</c>). Live only.</summary>
    public const string GcReaction = "gc.reaction";

    /// <summary>Client starts, refreshes or stops typing (doc 02, "Typing signal"). Answered with a <c>GcTypingResult</c>.</summary>
    public const string GcTyping = "gc.typing";

    /// <summary>Client asks for a project's Kanban board snapshot (doc 12, board view). Answered with a <c>KanbanBoardSnapshot</c>.</summary>
    public const string BoardSnapshot = "board.snapshot";

    /// <summary>Owner moves one Kanban card to another column, checked against the card's revision (doc 12). Answered with a <c>KanbanMoveReply</c>.</summary>
    public const string BoardMove = "board.move";

    /// <summary>Owner sets or clears one Kanban card's assignee, checked against the card's revision (doc 12). Answered with a <c>KanbanAssignReply</c>.</summary>
    public const string BoardAssign = "board.assign";

    /// <summary>Owner pauses one RyuseiFuda trigger (doc 38, Layer 2). Answered with a <c>RyuseiTriggerChangeReply</c>.</summary>
    public const string RyuseiPause = "ryusei.trigger.pause";

    /// <summary>Owner resumes one paused RyuseiFuda trigger (doc 38, Layer 2). Answered with a <c>RyuseiTriggerChangeReply</c>.</summary>
    public const string RyuseiResume = "ryusei.trigger.resume";

    /// <summary>Owner asks for a project's RyuseiFuda triggers as board chips (doc 38, Layer 2). Answered with a <c>RyuseiTriggerListReply</c>.</summary>
    public const string RyuseiList = "ryusei.trigger.list";

    /// <summary>Owner asks to grant one Hive (from project, to project, kind) pair (doc 25, "Hive grant wire request"). Answered with a <c>HiveGrantChangeReply</c>.</summary>
    public const string HiveGrantSet = "hive.grant.set";

    /// <summary>Owner asks to revoke one Hive (from project, to project, kind) pair (doc 25, "Hive grant wire request"). Answered with a <c>HiveGrantChangeReply</c>.</summary>
    public const string HiveGrantRevoke = "hive.grant.revoke";

    /// <summary>Owner lists PRs (doc 21, Client PR wire). Answered with a <c>PrListReply</c>.</summary>
    public const string PrList = "pr.list";

    /// <summary>Owner asks for one PR's summary. Answered with a <c>PrSummaryReply</c>.</summary>
    public const string PrSummary = "pr.summary";

    /// <summary>Owner asks for a file's hunks, keyed by head sha. Answered with a <c>PrHunksReply</c>.</summary>
    public const string PrHunks = "pr.hunks";

    /// <summary>Owner posts a comment or request-changes review; never approve. Answered with a <c>PrReviewReply</c>.</summary>
    public const string PrReview = "pr.review";

    /// <summary>Owner-signed squash merge (doc 21, Merge request path). Answered with a <c>PrMergeReply</c>.</summary>
    public const string PrMerge = "pr.merge";

    /// <summary>Client pages older entries.</summary>
    public const string GcHistory = "gc.history";

    /// <summary>Client asks for a GC rendered at a seq (doc 02, replay and fork requests). Answered with a <c>ReplayFrameResult</c>.</summary>
    public const string ReplayFrame = "replay.frame";

    /// <summary>Client forks a project GC at a seq. Answered with a <c>ReplayForkResult</c>.</summary>
    public const string ReplayFork = "replay.fork";

    /// <summary>Client marks a GC read up to a sequence.</summary>
    public const string GcRead = "gc.read";

    /// <summary>Replay gap too large; the client resyncs from the tail.</summary>
    public const string Resync = "resync";

    /// <summary>The server stopped live delivery on a slow stream.</summary>
    public const string Lagged = "lagged";

    /// <summary>Access to a stream was withdrawn.</summary>
    public const string StreamRevoked = "stream.revoked";

    /// <summary>Presence delta.</summary>
    public const string PresenceUpdate = "presence.update";

    /// <summary>Client asks for every agent's presence row. Answered with a <c>PresenceSnapshot</c> (docs/design/23).</summary>
    public const string PresenceSnapshot = "presence.snapshot";

    /// <summary>Level-of-detail feed for a graph stream (<c>graph:&lt;id&gt;</c>, doc 19).</summary>
    public const string GraphLod = "graph.lod";

    /// <summary>One terminal frame on a <c>pane:&lt;terminalId&gt;</c> stream, as JSON text (docs/design/55, section 6.1).</summary>
    public const string PaneFrame = "pane.frame";

    /// <summary>Client asks for its server-driven UI profile.</summary>
    public const string UiGet = "ui.get";

    /// <summary>The UI profile changed; carries a revision.</summary>
    public const string UiChanged = "ui.changed";

    /// <summary>Client sets a preference.</summary>
    public const string PrefsSet = "prefs.set";

    /// <summary>Client refreshes its credential.</summary>
    public const string AuthRefresh = "auth.refresh";

    /// <summary>Client registers a push token.</summary>
    public const string DeviceRegisterPush = "device.registerPush";

    /// <summary>Application-level keep-alive from a client.</summary>
    public const string Ping = "ping";

    /// <summary>Answer to <see cref="Ping"/>.</summary>
    public const string Pong = "pong";
}
