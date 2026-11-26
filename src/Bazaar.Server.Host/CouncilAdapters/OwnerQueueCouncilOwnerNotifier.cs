using Bazaar.Server.Council.Escalation;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// Production <see cref="ICouncilOwnerNotifier"/> (doc 10, Escalation): files one owner card through the approvals queue
/// (<see cref="IOwnerApprovalEnqueuer"/>, doc 15) per escalated vote and re-notify round. The card is a bare hint: it names the
/// vote id and the round, never the subject or the ballots, which the owner fetches over the authenticated connection.
/// Idempotent: the action key is <c>council-escalation:{voteId}:r{round}</c>, so a repeat, a restart or a retried pass makes no
/// second card. The round is the whole number of re-notify intervals since the vote escalated, so a later round files a fresh
/// card without any stored state.
/// </summary>
public sealed class OwnerQueueCouncilOwnerNotifier(
    IOwnerApprovalEnqueuer enqueuer,
    TimeProvider clock,
    CouncilEscalationOptions options,
    ICouncilEscalationRenotifySource? hoursSource = null) : ICouncilOwnerNotifier
{
    /// <summary>The tool name recorded on every escalation card.</summary>
    public const string ToolName = "council.escalation";

    /// <summary>Prefix of the action key.</summary>
    public const string ActionKeyPrefix = "council-escalation:";

    /// <summary>The approval agent id the cards are filed (and rate-capped) under: the Council's fixed system identity.</summary>
    public const string AgentId = "council";

    private readonly IOwnerApprovalEnqueuer _enqueuer = enqueuer ?? throw new ArgumentNullException(nameof(enqueuer));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly CouncilEscalationOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task NotifyEscalationAsync(EscalatedVote vote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vote);
        var hours = hoursSource is null ? _options.RenotifyHours : await hoursSource.GetRenotifyHoursAsync(ct).ConfigureAwait(false);
        var round = Round(vote.EscalatedAt, _clock.GetUtcNow(), hours);
        _ = await _enqueuer.EnqueueAsync(
            new OwnerApprovalEnqueueRequest(AgentId, ToolName, ActionKey(vote.VoteId, round), Summary(vote.VoteId, round)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Whole re-notify intervals since the escalation (0 for the first alert). Never negative; an interval below one hour counts as one.</summary>
    public static int Round(DateTimeOffset escalatedAt, DateTimeOffset now, int renotifyHours)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, renotifyHours));
        var age = now - escalatedAt;
        return age <= TimeSpan.Zero ? 0 : (int)(age / interval);
    }

    /// <summary>Stable action key for one vote and round.</summary>
    public static string ActionKey(Guid voteId, int round) => $"{ActionKeyPrefix}{voteId:N}:r{round}";

    /// <summary>What the owner reads on the card. No vote content.</summary>
    public static string Summary(Guid voteId, int round) =>
        round == 0
            ? $"Council vote {voteId:N} is escalated and awaits your decision."
            : $"Reminder {round}: council vote {voteId:N} is still escalated and awaits your decision.";
}
