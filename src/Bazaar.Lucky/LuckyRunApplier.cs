using System.Collections.Immutable;

namespace Bazaar.Lucky;

/// <summary>The outcome of applying a Lucky run plan.</summary>
/// <param name="GcId">The brainstorm GC.</param>
/// <param name="WakesInserted">Inbox rows inserted.</param>
/// <param name="CardIds">The Backlog cards created.</param>
public sealed record LuckyApplyResult(Guid GcId, int WakesInserted, ImmutableArray<Guid> CardIds);

/// <summary>
/// Applies a <see cref="LuckyRunPlan"/> through <see cref="ILuckyRunSink"/>, in this order: open the GC, append the ideas, enqueue
/// the wakes against the opening message, then create cards for owner-marked ideas. Not idempotent: applying the same plan twice
/// opens a second GC, so the caller applies each run once.
/// </summary>
public static class LuckyRunApplier
{
    /// <summary>Applies the plan.</summary>
    /// <param name="plan">The plan from <see cref="LuckyController.Plan"/>.</param>
    /// <param name="projectId">The project the cards belong to.</param>
    /// <param name="sink">The host's side-effect port.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<LuckyApplyResult> ApplyAsync(LuckyRunPlan plan, Guid projectId, ILuckyRunSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);

        var opened = await sink.OpenGcAsync(plan, ct).ConfigureAwait(false);

        var stored = plan.Entries.Length == 0
            ? []
            : await sink.AppendEntriesAsync(opened.GcId, plan.Entries, ct).ConfigureAwait(false);

        var wakesInserted = plan.Wakes.Length == 0
            ? 0
            : await sink.EnqueueWakesAsync(opened.GcId, opened.OpeningSeq, plan.Wakes, ct).ConfigureAwait(false);

        var messageByIdea = stored.ToDictionary(s => s.IdeaIndex, s => s.MessageId);
        var cardIds = ImmutableArray.CreateBuilder<Guid>(plan.Cards.Length);
        foreach (var card in plan.Cards)
        {
            if (!messageByIdea.TryGetValue(card.IdeaIndex, out var messageId))
            {
                throw new InvalidOperationException($"Idea {card.IdeaIndex} was not stored, so its card cannot be created.");
            }

            cardIds.Add(await sink.CreateCardAsync(projectId, opened.GcId, messageId, card.Title, ct).ConfigureAwait(false));
        }

        return new LuckyApplyResult(opened.GcId, wakesInserted, cardIds.ToImmutable());
    }
}
