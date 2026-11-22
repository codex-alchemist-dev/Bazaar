using System.Text.Json.Serialization;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.GitHub;

// Client PR wire (doc 21, "Client PR wire [PROPOSED]"). The server proxies GitHub; clients never see a token.
// Enums travel as snake_case strings so a client in any language reads them without a table.

/// <summary>Rolled-up state of a PR's checks at its head sha.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrChecksRollup>))]
public enum PrChecksRollup
{
    /// <summary>The PR has no checks at all. A merge treats this as not passing.</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>At least one check has not finished.</summary>
    [JsonStringEnumMemberName("pending")] Pending,

    /// <summary>Every check finished and passed.</summary>
    [JsonStringEnumMemberName("passing")] Passing,

    /// <summary>At least one check failed.</summary>
    [JsonStringEnumMemberName("failing")] Failing,
}

/// <summary>Which PRs <c>pr.list</c> returns.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrListState>))]
public enum PrListState
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("closed")] Closed,
    [JsonStringEnumMemberName("merged")] Merged,
    [JsonStringEnumMemberName("all")] All,
}

/// <summary>Whether a PR's author is an agent or a human.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrAuthorKind>))]
public enum PrAuthorKind
{
    [JsonStringEnumMemberName("agent")] Agent,
    [JsonStringEnumMemberName("human")] Human,
}

/// <summary>Payload of <c>pr.list</c>.</summary>
/// <param name="Repo">Optional <c>owner/repo</c> filter; null lists every watched repo.</param>
/// <param name="State">Which PRs to list.</param>
/// <param name="Cursor">Opaque paging cursor from a previous reply, or null for the first page.</param>
/// <param name="Limit">Page size hint; the server clamps it.</param>
[WirePayload(WireTypes.PrList)]
public sealed record PrListRequest(string? Repo, PrListState State, string? Cursor, int? Limit);

/// <summary>One row of the PR list.</summary>
/// <param name="PrId">The PR's GitHub node id (the <c>github_pr</c> key). Every other message names a PR by it.</param>
/// <param name="Repo"><c>owner/repo</c>.</param>
/// <param name="Number">PR number in that repo.</param>
/// <param name="Title">PR title.</param>
/// <param name="Author">Author login.</param>
/// <param name="AuthorKind">Agent or human.</param>
/// <param name="Draft">True while the PR is a draft.</param>
/// <param name="HeadSha">Current head sha. A client sends it back on hunks, review and merge.</param>
/// <param name="Checks">Checks rollup at <paramref name="HeadSha"/>.</param>
/// <param name="Revision">Monotonic revision of this row; it changes whenever the PR's head or state does.</param>
/// <param name="InstallationId">GitHub App installation that reads the repo. Null from a server that predates the identity fields; a client then cannot merge.</param>
/// <param name="Owner">Repo owner login, the first half of <paramref name="Repo"/>. Null from an older server.</param>
/// <param name="RepoName">Repo name, the second half of <paramref name="Repo"/>. Null from an older server.</param>
public sealed record PrListRow(
    string PrId,
    string Repo,
    int Number,
    string Title,
    string Author,
    PrAuthorKind AuthorKind,
    bool Draft,
    string HeadSha,
    PrChecksRollup Checks,
    long Revision,
    long? InstallationId = null,
    string? Owner = null,
    string? RepoName = null);

/// <summary>Reply to <c>pr.list</c>.</summary>
/// <param name="Rows">The page.</param>
/// <param name="NextCursor">Cursor for the next page, or null at the end.</param>
[WirePayload]
public sealed record PrListReply(IReadOnlyList<PrListRow> Rows, string? NextCursor);

/// <summary>Payload of <c>pr.summary</c>.</summary>
/// <param name="PrId">The PR.</param>
[WirePayload(WireTypes.PrSummary)]
public sealed record PrSummaryRequest(string PrId);

/// <summary>One changed file in a PR summary.</summary>
/// <param name="Path">Path in the repo.</param>
/// <param name="Status">GitHub's file status: added, modified, removed, renamed.</param>
/// <param name="Additions">Added lines.</param>
/// <param name="Deletions">Removed lines.</param>
public sealed record PrChangedFile(string Path, string Status, int Additions, int Deletions);

/// <summary>Reply to <c>pr.summary</c>.</summary>
/// <param name="PrId">The PR.</param>
/// <param name="HeadSha">Head sha every other field describes.</param>
/// <param name="Summary">Summary text.</param>
/// <param name="Files">Changed files.</param>
/// <param name="Checks">Checks rollup at <paramref name="HeadSha"/>.</param>
/// <param name="ReviewerApproved">Reviewer approval flag. It is data only, never merge authority (doc 21).</param>
/// <param name="LinkedCardId">The Kanban card this PR is tied to, or null.</param>
/// <param name="InstallationId">GitHub App installation for this PR, or null from an older server.</param>
/// <param name="Owner">Repo owner login, or null from an older server.</param>
/// <param name="RepoName">Repo name, or null from an older server.</param>
/// <param name="Number">PR number, or null from an older server.</param>
[WirePayload]
public sealed record PrSummaryReply(
    string PrId,
    string HeadSha,
    string Summary,
    IReadOnlyList<PrChangedFile> Files,
    PrChecksRollup Checks,
    bool ReviewerApproved,
    Guid? LinkedCardId,
    long? InstallationId = null,
    string? Owner = null,
    string? RepoName = null,
    int? Number = null);

/// <summary>Whether a head-keyed reply answered for the head the client asked about.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrHunksStatus>))]
public enum PrHunksStatus
{
    /// <summary>The hunks belong to the requested head sha.</summary>
    [JsonStringEnumMemberName("ok")] Ok,

    /// <summary>The head moved. The reply carries the current sha and no hunks.</summary>
    [JsonStringEnumMemberName("stale")] Stale,
}

/// <summary>Payload of <c>pr.hunks</c>.</summary>
/// <param name="PrId">The PR.</param>
/// <param name="HeadSha">The head sha the client is looking at.</param>
/// <param name="Path">File whose hunks are wanted.</param>
/// <param name="Cursor">Opaque paging cursor, or null for the first page.</param>
[WirePayload(WireTypes.PrHunks)]
public sealed record PrHunksRequest(string PrId, string HeadSha, string Path, string? Cursor);

/// <summary>One unified-diff hunk. <see cref="Lines"/> keep their leading space, plus or minus.</summary>
/// <param name="Header">The <c>@@ ... @@</c> line.</param>
/// <param name="OldStart">First old line.</param>
/// <param name="OldCount">Old line count.</param>
/// <param name="NewStart">First new line.</param>
/// <param name="NewCount">New line count.</param>
/// <param name="Lines">Hunk lines, each with its diff prefix.</param>
public sealed record PrHunk(string Header, int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<string> Lines);

/// <summary>Reply to <c>pr.hunks</c>.</summary>
/// <param name="Status">Ok, or stale when the requested head is not current.</param>
/// <param name="HeadSha">The PR's current head sha. Equals the requested one when <paramref name="Status"/> is ok.</param>
/// <param name="Path">File the hunks are for.</param>
/// <param name="Hunks">Hunks for <paramref name="HeadSha"/>; empty when stale.</param>
/// <param name="NextCursor">Cursor for the next page, or null at the end.</param>
[WirePayload]
public sealed record PrHunksReply(PrHunksStatus Status, string HeadSha, string Path, IReadOnlyList<PrHunk> Hunks, string? NextCursor);

/// <summary>Review event names a client may send. There is no approve: reviewer approval is data, not merge authority.</summary>
public static class PrReviewEvents
{
    /// <summary>A review comment.</summary>
    public const string Comment = "comment";

    /// <summary>A request for changes.</summary>
    public const string RequestChanges = "request_changes";
}

/// <summary>Payload of <c>pr.review</c>. <see cref="Event"/> is a string so an <c>approve</c> reaches the server and is answered <c>refused</c>.</summary>
/// <param name="PrId">The PR.</param>
/// <param name="HeadSha">The head sha the review is about.</param>
/// <param name="Event"><see cref="PrReviewEvents.Comment"/> or <see cref="PrReviewEvents.RequestChanges"/>.</param>
/// <param name="Body">Review text.</param>
[WirePayload(WireTypes.PrReview)]
public sealed record PrReviewRequest(string PrId, string HeadSha, string Event, string Body);

/// <summary>Outcome of <c>pr.review</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrReviewStatus>))]
public enum PrReviewStatus
{
    [JsonStringEnumMemberName("applied")] Applied,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("refused")] Refused,
}

/// <summary>Reply to <c>pr.review</c>.</summary>
/// <param name="Status">Applied, stale or refused.</param>
/// <param name="HeadSha">The PR's current head sha when known (always set for stale), else null.</param>
/// <param name="Message">Why a review was refused, or null.</param>
[WirePayload]
public sealed record PrReviewReply(PrReviewStatus Status, string? HeadSha, string? Message);

/// <summary>
/// The shared owner payload (<c>bazaar.owner.v1</c>, doc 32 section 5a) as it travels. The client signs the exact bytes the
/// server rebuilds from these fields. <see cref="Action"/> is <c>pr.merge</c> and <see cref="ArgsHash"/> is <c>OwnerMergeArgs.Hash</c>.
/// </summary>
/// <param name="Action">Action name; <c>pr.merge</c>.</param>
/// <param name="ApprovalId">Unique id of this approval.</param>
/// <param name="ArgsHash">SHA-256 of the PR and merge request (base64 on the wire).</param>
/// <param name="HeadSha">Head sha being approved.</param>
/// <param name="HumanId">The owner's human id.</param>
/// <param name="DeviceId">The signing device.</param>
/// <param name="IssuedAt">When signed.</param>
/// <param name="ExpiresAt">When the approval lapses.</param>
/// <param name="Nonce">Single-use nonce (base64 on the wire).</param>
public sealed record PrOwnerPayload(
    string Action,
    Guid ApprovalId,
    byte[] ArgsHash,
    string? HeadSha,
    Guid HumanId,
    string DeviceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    byte[] Nonce);

/// <summary>Payload of <c>pr.merge</c>. Only the owner's own device may build it; dictation never triggers it.</summary>
/// <param name="PrId">The PR.</param>
/// <param name="HeadSha">The head sha the owner approved.</param>
/// <param name="CommitTitle">Squash commit title.</param>
/// <param name="CommitMessage">Squash commit message, including any sign-off line; the server adds nothing.</param>
/// <param name="Owner">The signed owner payload.</param>
/// <param name="Signature">Owner device signature over the payload (base64 on the wire).</param>
[WirePayload(WireTypes.PrMerge)]
public sealed record PrMergeRequest(
    string PrId,
    string HeadSha,
    string CommitTitle,
    string CommitMessage,
    PrOwnerPayload Owner,
    byte[] Signature);

/// <summary>Outcome of <c>pr.merge</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrMergeStatus>))]
public enum PrMergeStatus
{
    /// <summary>The squash merge happened.</summary>
    [JsonStringEnumMemberName("merged")] Merged,

    /// <summary>The head moved. The owner must re-approve against the current sha.</summary>
    [JsonStringEnumMemberName("stale")] Stale,

    /// <summary>Checks at the head are not passing.</summary>
    [JsonStringEnumMemberName("checks_failed")] ChecksFailed,

    /// <summary>The owner signature was not accepted (forged, expired, unknown or revoked device, or replayed).</summary>
    [JsonStringEnumMemberName("bad_signature")] BadSignature,

    /// <summary>GitHub refused the merge as not mergeable (HTTP 405 or 422): branch protection, required reviews, or a closed PR.</summary>
    [JsonStringEnumMemberName("not_mergeable")] NotMergeable,

    /// <summary>GitHub refused the merge on a conflict (HTTP 409): the head moved at GitHub or the branch conflicts.</summary>
    [JsonStringEnumMemberName("conflict")] Conflict,

    /// <summary>Anything else: wrong action, approval for a different merge, malformed request.</summary>
    [JsonStringEnumMemberName("refused")] Refused,
}

/// <summary>Reply to <c>pr.merge</c>.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="HeadSha">The PR's current head sha when known (always set for stale), else null.</param>
/// <param name="Message">Short reason for a non-merged outcome, or null.</param>
[WirePayload]
public sealed record PrMergeReply(PrMergeStatus Status, string? HeadSha, string? Message);
