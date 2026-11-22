namespace Bazaar.Server.WorkLife;

/// <summary>
/// Availability input to presence (doc 23, "Activity and presence"). Mirrors the states of doc 09.
/// </summary>
public enum PresenceAvailability
{
    OnShift,
    Draining,
    Break,
    Off,
    Vacation,
}

/// <summary>The current activity of an agent's turn (doc 23, from doc 06).</summary>
public enum PresenceActivityKind
{
    Idle,
    ModelCall,
    Tool,

    /// <summary>Waiting on a reply, an approval, or a Kanban prerequisite.</summary>
    Waiting,
    Drafting,
}

/// <summary>Class of the tool in use. Only meaningful when the activity is <see cref="PresenceActivityKind.Tool"/>.</summary>
public enum PresenceToolClass
{
    None,
    Research,
    Review,
    EditOrBuild,
}

/// <summary>The kind of GC the current turn runs in.</summary>
public enum PresenceGcContext
{
    Other,
    CouncilOrVote,
    OtherProjectGc,
    ProjectGc,
}

/// <summary>Base presence labels (doc 23, DECISIONS-LOG C43). The idle label is supplied by the caller.</summary>
public enum PresenceLabel
{
    OnVacation,
    Sleeping,
    InMeeting,
    Researching,
    Reviewing,
    Thinking,
    Working,
    HelpingAnotherProject,
}

/// <summary>
/// Overlays shown beside the base label. <see cref="Ward"/> is listed for the display set, but this
/// derivation never sets it: the Psych Ward state (doc 26) is owned elsewhere.
/// </summary>
[Flags]
public enum PresenceOverlay
{
    None = 0,
    OnBreak = 1 << 0,
    WaitingForApproval = 1 << 1,
    Throttled = 1 << 2,
    Covered = 1 << 3,
    Ward = 1 << 4,
    Idle = 1 << 5,
}

/// <summary>
/// Everything <see cref="PresenceRules.Derive"/> reads for one agent at one moment.
/// </summary>
/// <param name="Availability">The availability state (doc 09).</param>
/// <param name="Activity">The current turn's activity.</param>
/// <param name="ToolClass">The tool class; read only for <see cref="PresenceActivityKind.Tool"/>.</param>
/// <param name="GcContext">The GC the current turn runs in.</param>
/// <param name="Throttled">True while the agent is THROTTLED (doc 09).</param>
/// <param name="Covered">True while a cover is answering for the agent (doc 09).</param>
/// <param name="WaitingForApproval">True when a Waiting turn is waiting on an approval.</param>
public sealed record PresenceInput(
    PresenceAvailability Availability,
    PresenceActivityKind Activity,
    PresenceToolClass ToolClass,
    PresenceGcContext GcContext,
    bool Throttled = false,
    bool Covered = false,
    bool WaitingForApproval = false);

/// <summary>The derived presence: a label and the overlays shown beside it.</summary>
public sealed record PresenceResult(string Label, PresenceOverlay Overlays);

/// <summary>
/// Pure presence derivation (doc 23, "Activity and presence"). It reads no clock, does no I/O and
/// keeps no state, so every row of the table is testable.
/// </summary>
/// <remarks>
/// Precedence, in order:
/// <list type="number">
/// <item>Vacation gives "on vacation", whatever the activity.</item>
/// <item>Off or Break with idle, drafting or waiting activity gives "sleeping". Break adds <see cref="PresenceOverlay.OnBreak"/>.</item>
/// <item>
/// Off or Break with a turn (model call or tool) uses the on-shift rows below. This is a choice the doc
/// does not spell out: an agent that is working while off shift shows what it is doing rather than
/// "sleeping". Break still adds <see cref="PresenceOverlay.OnBreak"/>.
/// </item>
/// <item>
/// On shift or draining, in this order: a turn in a Council or vote GC gives "in meeting"; a tool in the
/// research class gives "researching"; a tool in the review class gives "reviewing"; a model call gives
/// "thinking"; a tool in the edit or build class gives "working"; a turn in another project's GC gives
/// "helping another project"; anything else (idle, drafting, or a turn with no tool class) gives the idle label.
/// </item>
/// <item>Waiting uses the idle rule.</item>
/// <item>
/// Overlays: <see cref="PresenceOverlay.WaitingForApproval"/> when the activity is Waiting and an approval is
/// pending; <see cref="PresenceOverlay.Throttled"/> and <see cref="PresenceOverlay.Covered"/> whenever set; and
/// <see cref="PresenceOverlay.OnBreak"/> during Break. <see cref="PresenceOverlay.Idle"/> is added only on the
/// idle rule, and only when the idle label is "working".
/// </item>
/// </list>
/// Design notes, where doc 23 leaves the choice open:
/// <list type="bullet">
/// <item>
/// A tool with no tool class (<see cref="PresenceToolClass.None"/>) is a turn, so it shows "working" rather than the
/// idle label. It is not idle.
/// </item>
/// <item>
/// Off or Break with waiting or drafting activity shows "sleeping". The doc gives the idle rule for on-shift
/// agents only, and an agent that is off shift should not show a working label.
/// </item>
/// <item>
/// The "helping another project" row sits after the activity rows, as the doc lists it. So under the listed order a
/// model call in another project's GC shows "thinking", and a tool call with edit or build class shows "working".
/// Only a tool call with no tool class reaches "helping another project" in that GC. Doc 23 does not settle this
/// ordering, and the result is close to unreachable, so it is open for the owner. Moving the other-project GC check
/// above the activity rows (as the council check is) is a one-line change in <c>TurnLabel</c>.
/// </item>
/// </list>
/// </remarks>
public static class PresenceRules
{
    /// <summary>The shipped default of <c>presence.idle_label</c> (doc 09).</summary>
    public const string DefaultIdleLabel = "working";

    /// <summary>Returns the text shown for a base label.</summary>
    public static string LabelText(PresenceLabel label) => label switch
    {
        PresenceLabel.OnVacation => "on vacation",
        PresenceLabel.Sleeping => "sleeping",
        PresenceLabel.InMeeting => "in meeting",
        PresenceLabel.Researching => "researching",
        PresenceLabel.Reviewing => "reviewing",
        PresenceLabel.Thinking => "thinking",
        PresenceLabel.Working => "working",
        PresenceLabel.HelpingAnotherProject => "helping another project",
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
    };

    /// <summary>
    /// Derives the presence for one agent.
    /// </summary>
    /// <param name="input">The availability, activity, GC context and overlay flags.</param>
    /// <param name="idleLabel">The text for idle, drafting and waiting on shift (<c>presence.idle_label</c>). Must not be empty or whitespace.</param>
    /// <exception cref="ArgumentException"><paramref name="idleLabel"/> is null, empty or whitespace.</exception>
    public static PresenceResult Derive(PresenceInput input, string idleLabel)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(idleLabel);

        var overlays = PresenceOverlay.None;
        if (input.Throttled)
        {
            overlays |= PresenceOverlay.Throttled;
        }

        if (input.Covered)
        {
            overlays |= PresenceOverlay.Covered;
        }

        if (input.Availability == PresenceAvailability.Break)
        {
            overlays |= PresenceOverlay.OnBreak;
        }

        if (input.Activity == PresenceActivityKind.Waiting && input.WaitingForApproval)
        {
            overlays |= PresenceOverlay.WaitingForApproval;
        }

        if (input.Availability == PresenceAvailability.Vacation)
        {
            return new PresenceResult(LabelText(PresenceLabel.OnVacation), overlays);
        }

        var isTurn = input.Activity is PresenceActivityKind.ModelCall or PresenceActivityKind.Tool;
        if (!isTurn && input.Availability is PresenceAvailability.Off or PresenceAvailability.Break)
        {
            return new PresenceResult(LabelText(PresenceLabel.Sleeping), overlays);
        }

        var turnLabel = TurnLabel(input);
        if (turnLabel is not null)
        {
            return new PresenceResult(LabelText(turnLabel.Value), overlays);
        }

        // Idle rule: idle, drafting, waiting, or a turn with no tool class that is not in another project's GC.
        if (idleLabel == DefaultIdleLabel)
        {
            overlays |= PresenceOverlay.Idle;
        }

        return new PresenceResult(idleLabel, overlays);
    }

    /// <summary>
    /// The label for a turn (model call or tool), in the precedence order of the on-shift rows. Returns null when
    /// the activity is not a turn (idle, waiting or drafting), so the caller applies the idle rule.
    /// </summary>
    private static PresenceLabel? TurnLabel(PresenceInput input)
    {
        var isTurn = input.Activity is PresenceActivityKind.ModelCall or PresenceActivityKind.Tool;
        if (!isTurn)
        {
            return null;
        }

        if (input.GcContext == PresenceGcContext.CouncilOrVote)
        {
            return PresenceLabel.InMeeting;
        }

        if (input.Activity == PresenceActivityKind.Tool)
        {
            switch (input.ToolClass)
            {
                case PresenceToolClass.Research:
                    return PresenceLabel.Researching;
                case PresenceToolClass.Review:
                    return PresenceLabel.Reviewing;
            }
        }

        if (input.Activity == PresenceActivityKind.ModelCall)
        {
            return PresenceLabel.Thinking;
        }

        if (input.Activity == PresenceActivityKind.Tool && input.ToolClass == PresenceToolClass.EditOrBuild)
        {
            return PresenceLabel.Working;
        }

        if (input.GcContext == PresenceGcContext.OtherProjectGc)
        {
            return PresenceLabel.HelpingAnotherProject;
        }

        // A tool call with no tool class is still a turn, so it is working rather than idle.
        return PresenceLabel.Working;
    }
}
