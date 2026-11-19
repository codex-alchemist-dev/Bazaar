namespace Bazaar.Diagnostics.Grouping;

/// <summary>
/// Where a crash group stands on the issue side (doc 43 section 8.4). The three values are the ones the design
/// needs for decisions. Doc 8.4's "filed" is <see cref="Open"/> with an issue number, and "reopened" is not a
/// status: a regression reopens the issue and the group becomes <see cref="ResolvedIn"/>, as the doc says.
/// </summary>
public enum GroupStatus
{
    /// <summary>The group has an issue, or a filing was requested in this update and the number is not recorded yet.</summary>
    Open,

    /// <summary>The group needs an issue but the daily filing budget is used up. It waits and is never dropped.</summary>
    FilingDeferred,

    /// <summary>
    /// The issue was closed with a fixed version and an occurrence arrived at or above that version, so the issue
    /// was reopened. The group keeps this status until the next change of state, and the fix version stays recorded.
    /// </summary>
    ResolvedIn,
}
