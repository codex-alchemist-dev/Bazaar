using System.Collections.Immutable;

namespace Bazaar.Lucky;

/// <summary>The outcome of a start request.</summary>
public enum LuckyStartStatus
{
    /// <summary>The run started and its side effects were applied.</summary>
    Started,

    /// <summary>The requester is not the owner. Nothing was stored or applied.</summary>
    NotOwner,

    /// <summary>The request was refused, for example by the daily cap or an empty prompt.</summary>
    Refused,
}

/// <summary>What a start request produced.</summary>
/// <param name="Status">Started, not the owner, or refused.</param>
/// <param name="RunId">The new run, when started.</param>
/// <param name="GcId">The brainstorm GC, when started.</param>
/// <param name="Participants">Agents invited, when started.</param>
/// <param name="WakesInserted">Brainstorm wakes written, when started.</param>
/// <param name="Refusal">Why the request was refused, when it was.</param>
public sealed record LuckyStartOutcome(
    LuckyStartStatus Status,
    string? RunId,
    Guid? GcId,
    int Participants,
    int WakesInserted,
    string? Refusal);

/// <summary>
/// The owner's start path (docs/design/26-fun-systems.md, section 6). It enforces owner-only starts and the daily cap, takes the
/// candidates from the server's candidate source, stores the run with its GC, and applies the plan through the host's sink.
/// </summary>
public sealed class LuckyStartService(LuckyController controller, ILuckyRunStore store, ILuckyRunSink sink, ILuckyCandidateSource candidates)
{
    /// <summary>Starts a run for the owner, or refuses it.</summary>
    /// <param name="isOwner">True only when the requester is the owner principal.</param>
    /// <param name="ownerId">The owner's id.</param>
    /// <param name="projectId">The project the run belongs to.</param>
    /// <param name="prompt">The owner's prompt.</param>
    /// <param name="now">The start time.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LuckyStartOutcome> StartAsync(
        bool isOwner, Guid ownerId, Guid projectId, string prompt, DateTimeOffset now, CancellationToken ct)
    {
        if (!isOwner)
        {
            return new LuckyStartOutcome(LuckyStartStatus.NotOwner, null, null, 0, 0, null);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Refused("A Lucky run needs a prompt.");
        }

        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var runsToday = await store.CountStartedOnAsync(ownerId, day, ct).ConfigureAwait(false);
        var runId = Guid.CreateVersion7().ToString("N");
        var offered = await candidates.ListAsync(ct).ConfigureAwait(false);

        var result = controller.Start(runId, prompt, requestedByOwner: true, now, runsToday, offered, () => Random.Shared.Next());
        if (!result.Accepted)
        {
            return Refused(result.Refusal ?? "The run was refused.");
        }

        var run = result.Run!;
        var applied = await LuckyRunApplier.ApplyAsync(controller.Plan(run), projectId, ownerId, sink, ct).ConfigureAwait(false);
        await store.SaveAsync(new LuckyStoredRun(run, projectId, ownerId, day, applied.GcId, ImmutableDictionary<int, Guid>.Empty), ct)
            .ConfigureAwait(false);

        return new LuckyStartOutcome(
            LuckyStartStatus.Started, runId, applied.GcId, run.Participants.Length, applied.WakesInserted, null);
    }

    private static LuckyStartOutcome Refused(string reason) =>
        new(LuckyStartStatus.Refused, null, null, 0, 0, reason);
}
