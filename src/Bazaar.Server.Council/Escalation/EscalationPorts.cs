namespace Bazaar.Server.Council.Escalation;

/// <summary>
/// Storage port: the votes currently in Escalated. Implemented by <see cref="Data.CouncilStore"/>; kept apart from
/// <see cref="Service.ICouncilStore"/> so the escalation surface adds no member to that port.
/// </summary>
public interface ICouncilEscalationSource
{
    /// <summary>Ids of votes in state Escalated, oldest escalation first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> ListEscalatedAsync(int limit, CancellationToken ct);
}

/// <summary>
/// Port to the owner alert (docs/design/10, Escalation: the owner-decision wake, a bare hint with the card fetched over the
/// authenticated connection). NO IMPLEMENTATION EXISTS YET: the server has no owner-decision notifier (only the client-side
/// <c>PushWake</c> parser and <c>ILocalNotifier</c>). A host registers one when that path lands; until then the re-notify
/// worker finds no notifier and does nothing. The Council module invents no notifier of its own.
/// </summary>
public interface ICouncilOwnerNotifier
{
    /// <summary>Alerts the owner that <paramref name="vote"/> awaits a decision. Carries no vote content on the wire.</summary>
    Task NotifyEscalationAsync(EscalatedVote vote, CancellationToken ct);
}

/// <summary>
/// Port to the live value of <c>council.escalation_renotify_hours</c> (doc 10 Settings, default 4). The host adapter reads the
/// settings registry, so an operator change applies without a restart. Without an adapter
/// <see cref="CouncilEscalationOptions.RenotifyHours"/> stands.
/// </summary>
public interface ICouncilEscalationRenotifySource
{
    /// <summary>Reads the current interval in hours (at least 1).</summary>
    Task<int> GetRenotifyHoursAsync(CancellationToken ct);
}

/// <summary>Fallback install values for the escalation surface. The setting key is <c>council.escalation_renotify_hours</c>.</summary>
public sealed record CouncilEscalationOptions
{
    /// <summary>Hours between owner alerts for one escalated vote when no <see cref="ICouncilEscalationRenotifySource"/> is registered. Doc 10 default 4.</summary>
    public int RenotifyHours { get; init; } = 4;

    /// <summary>Time between two passes of the re-notify worker.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Most escalated votes read per pass.</summary>
    public int BatchSize { get; init; } = 100;
}
