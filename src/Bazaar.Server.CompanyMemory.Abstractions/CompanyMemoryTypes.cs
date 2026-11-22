using Bazaar.Server.Knowledge.CompanyMemory;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// A decision an agent wants on the company record (doc 25 Company Memory, doc 18 decision records). The Vault owns the
/// file; this is only what the writer needs.
/// </summary>
/// <param name="DocId">Idempotency key: recording the same id twice returns the first record and writes nothing.</param>
/// <param name="Slug">Lowercase words joined by hyphens, used in the file name.</param>
/// <param name="Title">Front matter title. The Vault forbids <c>#</c>, <c>|</c>, <c>^</c> and <c>:</c> in it.</param>
/// <param name="Body">Markdown: what was decided and why.</param>
/// <param name="AgentId">The agent recording it; goes to the commit trailer.</param>
/// <param name="Supersedes">Existing decisions this one replaces: <c>0001</c>, <c>0001-slug</c> or a <c>decisions/</c> path.</param>
/// <param name="DecidedIn">The GC message that decided it, or null.</param>
public sealed record RecordDecisionRequest(
    Guid DocId,
    string Slug,
    string Title,
    string Body,
    string AgentId,
    IReadOnlyList<string>? Supersedes = null,
    string? DecidedIn = null);

/// <summary>A decision as stored.</summary>
/// <param name="Path">The Vault path, such as <c>decisions/0002-use-notify.md</c>.</param>
/// <param name="Number">The decision number.</param>
/// <param name="AlreadyRecorded">True when the request id had been recorded before and nothing new was written.</param>
public sealed record RecordedDecision(string Path, int Number, bool AlreadyRecorded);

/// <summary>
/// The chain around one decision. <see cref="Older"/> lists what it replaced, nearest first. <see cref="Newer"/> lists what
/// replaced it, nearest first. <see cref="Links"/> holds every edge between those decisions and the focus. A target that is
/// not in the Vault stays in the lists with its link flagged, so the view can show a missing source.
/// </summary>
/// <param name="Focus">The record that was asked for.</param>
/// <param name="Older">Decisions the focus replaced, directly or through other decisions.</param>
/// <param name="Newer">Decisions that replaced the focus, directly or through other decisions.</param>
/// <param name="Links">The edges inside this chain, sorted by from, then to.</param>
/// <param name="Current">The newest decisions in the chain that nothing replaces; usually one.</param>
public sealed record DecisionChainView(
    DecisionRecord Focus,
    IReadOnlyList<string> Older,
    IReadOnlyList<string> Newer,
    IReadOnlyList<DecisionLink> Links,
    IReadOnlyList<string> Current);
