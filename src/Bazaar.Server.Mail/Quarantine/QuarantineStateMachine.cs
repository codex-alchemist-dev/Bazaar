namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// The legal quarantine transitions (doc 22, Quarantine and reporting). Pure: no I/O. Owner-only release and the reader's
/// inability to release are floors, not settings. Held to released keeps the taint (<see cref="QuarantineItem.Tainted"/>).
/// </summary>
public static class QuarantineStateMachine
{
    /// <summary>Whether this actor may add an item to quarantine. A reader session can only ever add; an agent cannot.</summary>
    public static bool CanAdmit(QuarantineActor actor) =>
        actor.Kind is QuarantineActorKind.Reader or QuarantineActorKind.System;

    /// <summary>Decides one transition for an item. Returns the item in its new state when allowed.</summary>
    public static QuarantineTransitionResult Apply(
        QuarantineItem item, QuarantineState to, QuarantineActor actor, QuarantineTransitionContext context)
    {
        if (actor.Kind == QuarantineActorKind.Agent)
        {
            return Refuse(item, QuarantineTransitionOutcome.AgentCannotChange);
        }

        if (item.State != QuarantineState.Held)
        {
            return Refuse(item, QuarantineTransitionOutcome.IllegalTransition);
        }

        switch (to)
        {
            case QuarantineState.Released:
                if (actor.Kind == QuarantineActorKind.Reader)
                {
                    return Refuse(item, QuarantineTransitionOutcome.ReaderCannotRelease);
                }

                return IsOwner(actor)
                    ? Allow(item, to)
                    : Refuse(item, QuarantineTransitionOutcome.NotOwner);

            case QuarantineState.Reported:
                if (actor.Kind == QuarantineActorKind.Reader)
                {
                    return Refuse(item, QuarantineTransitionOutcome.ReaderCannotRelease);
                }

                if (!IsOwner(actor))
                {
                    return Refuse(item, QuarantineTransitionOutcome.NotOwner);
                }

                return context.ReportApprovalId is { } id && id != Guid.Empty
                    ? Allow(item, to)
                    : Refuse(item, QuarantineTransitionOutcome.ReportNeedsApproval);

            case QuarantineState.Purged:
                if (actor.Kind != QuarantineActorKind.System)
                {
                    return Refuse(item, QuarantineTransitionOutcome.NotOwner);
                }

                if (context.LegalHold)
                {
                    return Refuse(item, QuarantineTransitionOutcome.LegalHold);
                }

                return context.RetentionDue
                    ? Allow(item, to)
                    : Refuse(item, QuarantineTransitionOutcome.NotDueForPurge);

            default:
                return Refuse(item, QuarantineTransitionOutcome.IllegalTransition);
        }
    }

    private static bool IsOwner(QuarantineActor actor) =>
        actor.Kind == QuarantineActorKind.Owner && actor.HumanId is { } id && id != Guid.Empty;

    private static QuarantineTransitionResult Allow(QuarantineItem item, QuarantineState to) =>
        new(QuarantineTransitionOutcome.Allowed, item with { State = to });

    private static QuarantineTransitionResult Refuse(QuarantineItem item, QuarantineTransitionOutcome outcome) =>
        new(outcome, item);
}
