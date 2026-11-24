namespace Bazaar.Server.Interrupts;

/// <summary>
/// Pure per-viewer reason redaction (doc 24 section 5, "Interrupt reasons on the read route"). A reason is a personal field.
/// The company owner reads every reason, and an agent reads the reasons of its own frames. Everyone else, and the anonymous
/// viewer, sees <see cref="RedactedReason"/>; the rest of the frame is left alone so depth and source stay visible.
/// </summary>
public static class InterruptVisibilityRules
{
    /// <summary>Reason shown in place of one the viewer may not read.</summary>
    public const string RedactedReason = "(private)";

    /// <summary>True when <paramref name="viewer"/> may read the reason of <paramref name="frame"/>.</summary>
    public static bool CanSeeReason(InterruptFrame frame, InterruptViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.IsCompanyOwner)
            return true;

        // The empty id is the anonymous viewer and never matches.
        return viewer.Id != Guid.Empty && frame.AgentId == viewer.Id;
    }

    /// <summary>The reason <paramref name="viewer"/> is shown for <paramref name="frame"/>.</summary>
    public static string ReasonFor(InterruptFrame frame, InterruptViewer viewer) =>
        CanSeeReason(frame, viewer) ? frame.Reason : RedactedReason;
}
