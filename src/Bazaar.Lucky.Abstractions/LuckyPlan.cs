using System.Collections.Immutable;

namespace Bazaar.Lucky;

/// <summary>A wake the host enqueues for one opted-in participant (doc 06 brainstorm reason, once per run).</summary>
/// <param name="AgentId">The agent to wake.</param>
/// <param name="Kind">The inbox kind; always <c>brainstorm</c>.</param>
/// <param name="DedupeKey">The idempotency key, unique per run and agent, so a repeat enqueue is a no-op (doc 06).</param>
public sealed record LuckyWake(string AgentId, string Kind, string DedupeKey);

/// <summary>An entry for the ephemeral brainstorm GC: an idea, tagged with its source trust (doc 26, section 6).</summary>
/// <param name="Kind">Always <c>idea</c>.</param>
/// <param name="SourceTrust">Always <c>agent</c>.</param>
/// <param name="AgentId">The author.</param>
/// <param name="Text">The sanitized idea text. Quarantined ideas are never planned.</param>
/// <param name="IdeaIndex">The idea's index in the run, so a card can point back to its message.</param>
public sealed record LuckyGcEntry(string Kind, string SourceTrust, string AgentId, string Text, int IdeaIndex);

/// <summary>A Backlog card the host creates for an owner-marked idea (doc 26 section 6, doc 12).</summary>
/// <param name="RunId">The run the idea came from.</param>
/// <param name="IdeaIndex">The idea's index in the run.</param>
/// <param name="Title">The card title: the idea text, cut to a short line.</param>
/// <param name="Column">Always <c>backlog</c>. An idea never starts work.</param>
public sealed record LuckyCardRequest(string RunId, int IdeaIndex, string Title, string Column);

/// <summary>
/// Everything the host needs to run a Lucky run's side effects. The planner is pure; the host enqueues the wakes, opens the
/// GC, appends the entries and creates the cards.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="GcTypeKey">The GC type for the run; <c>brainstorm</c>, which is ephemeral.</param>
/// <param name="Ephemeral">True: the GC is an ephemeral brainstorm GC.</param>
/// <param name="Prompt">The owner's prompt, used as the GC title.</param>
/// <param name="Wakes">One wake per opted-in participant.</param>
/// <param name="Entries">The kept ideas in the order recorded.</param>
/// <param name="Cards">Backlog cards for owner-marked kept ideas, only under <c>owner_marked</c>.</param>
public sealed record LuckyRunPlan(
    string RunId,
    string GcTypeKey,
    bool Ephemeral,
    string Prompt,
    ImmutableArray<LuckyWake> Wakes,
    ImmutableArray<LuckyGcEntry> Entries,
    ImmutableArray<LuckyCardRequest> Cards);
