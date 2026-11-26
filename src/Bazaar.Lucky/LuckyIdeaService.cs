using System.Collections.Immutable;
using Bazaar.Sanitization;

namespace Bazaar.Lucky;

/// <summary>The outcome of an idea or mark request.</summary>
public enum LuckyActStatus
{
    /// <summary>The request was applied.</summary>
    Done,

    /// <summary>The run does not exist.</summary>
    NotFound,

    /// <summary>The requester is not allowed to do this.</summary>
    NotAllowed,

    /// <summary>The request was refused by a rule, such as the time box or the idea limit.</summary>
    Refused,
}

/// <summary>What an idea or mark request produced.</summary>
/// <param name="Status">The result.</param>
/// <param name="IdeaIndex">The idea's index, when applied.</param>
/// <param name="Quarantined">True when the idea was quarantined.</param>
/// <param name="CardId">The Backlog card, for a mark.</param>
/// <param name="Reason">Why it was refused, when it was.</param>
public sealed record LuckyActOutcome(LuckyActStatus Status, int IdeaIndex, bool Quarantined, Guid? CardId, string? Reason);

/// <summary>
/// Records participants' ideas and the owner's marks (docs/design/26-fun-systems.md, section 6). A kept idea is appended to the
/// brainstorm GC at once, and a marked idea becomes a Backlog card from that message. Ideas never become work.
/// </summary>
public sealed class LuckyIdeaService(LuckyController controller, ILuckyRunStore store, ILuckyRunSink sink)
{
    /// <summary>Records one idea from a participating agent.</summary>
    /// <param name="agentId">The agent's id, from its principal.</param>
    /// <param name="runId">The run.</param>
    /// <param name="text">The idea text.</param>
    /// <param name="now">The time the idea was recorded.</param>
    /// <param name="terms">The sanitizer's sensitive terms.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LuckyActOutcome> RecordIdeaAsync(Guid agentId, string runId, string text, DateTimeOffset now, SensitiveTerms terms, CancellationToken ct)
    {
        var stored = await store.LoadAsync(runId, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return new LuckyActOutcome(LuckyActStatus.NotFound, -1, false, null, "No run has that id.");
        }

        var result = controller.RecordIdea(stored.Run, agentId.ToString(), text, now, terms);
        if (!result.Accepted)
        {
            return new LuckyActOutcome(LuckyActStatus.Refused, -1, false, null, result.Refusal);
        }

        var updated = result.Run!;
        var index = updated.Ideas.Length - 1;
        var idea = updated.Ideas[index];
        var messages = stored.Messages;

        // Quarantined ideas are recorded for the limit but never reach the GC.
        if (idea.Status == LuckyIdeaStatus.Kept)
        {
            if (stored.GcId is not Guid gcId)
            {
                return new LuckyActOutcome(LuckyActStatus.Refused, index, false, null, "This run has no brainstorm GC yet.");
            }

            var entry = new LuckyGcEntry("idea", LuckyIdea.SourceTrust, agentId.ToString(), idea.Text, index);
            var appended = await sink.AppendEntriesAsync(gcId, [entry], ct).ConfigureAwait(false);
            messages = messages.SetItem(index, appended[0].MessageId);
        }

        await store.SaveAsync(stored with { Run = updated, IdeaMessages = messages }, ct).ConfigureAwait(false);
        return new LuckyActOutcome(LuckyActStatus.Done, index, idea.Status == LuckyIdeaStatus.Quarantined, null, null);
    }

    /// <summary>Marks a kept idea for a Backlog card. Only the owner may mark, and re-marking is safe.</summary>
    /// <param name="isOwner">True only for the owner principal.</param>
    /// <param name="runId">The run.</param>
    /// <param name="ideaIndex">The idea's index.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LuckyActOutcome> MarkIdeaAsync(bool isOwner, string runId, int ideaIndex, CancellationToken ct)
    {
        if (!isOwner)
        {
            return new LuckyActOutcome(LuckyActStatus.NotAllowed, ideaIndex, false, null, null);
        }

        var stored = await store.LoadAsync(runId, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return new LuckyActOutcome(LuckyActStatus.NotFound, ideaIndex, false, null, "No run has that id.");
        }

        var result = controller.MarkForCard(stored.Run, ideaIndex, markedByOwner: true);
        if (!result.Accepted)
        {
            return new LuckyActOutcome(LuckyActStatus.Refused, ideaIndex, false, null, result.Refusal);
        }

        if (!stored.Messages.TryGetValue(ideaIndex, out var messageId) || stored.GcId is not Guid gcId)
        {
            return new LuckyActOutcome(LuckyActStatus.Refused, ideaIndex, false, null, "That idea is not in the brainstorm GC.");
        }

        var idea = result.Run!.Ideas[ideaIndex];
        var title = idea.Text.Length <= LuckyController.CardTitleLimit ? idea.Text : idea.Text[..LuckyController.CardTitleLimit];
        // The sink keys the card to its message, so a repeated mark returns the same card.
        var cardId = await sink.CreateCardAsync(stored.ProjectId, gcId, messageId, title, ct).ConfigureAwait(false);

        await store.SaveAsync(stored with { Run = result.Run! }, ct).ConfigureAwait(false);
        return new LuckyActOutcome(LuckyActStatus.Done, ideaIndex, false, cardId, null);
    }
}
