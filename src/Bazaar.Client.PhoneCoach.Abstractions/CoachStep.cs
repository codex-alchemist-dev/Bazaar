namespace Bazaar.Client.PhoneCoach;

/// <summary>The gesture a coach step teaches.</summary>
public enum CoachGesture
{
    /// <summary>Swipe left or right between chats or panes.</summary>
    Swipe,

    /// <summary>Hold empty space for the radial menu, or the composer for dictation.</summary>
    Hold,
}

/// <summary>One short prompt of the first-run coach. Doc 93 asks for two: swipe, then hold.</summary>
/// <param name="Gesture">What the step teaches; the view picks its glyph from it.</param>
/// <param name="Title">The heading, a few words.</param>
/// <param name="Body">One sentence naming the gesture and what it does.</param>
public sealed record CoachStep(CoachGesture Gesture, string Title, string Body)
{
    /// <summary>The two shipped steps, in order. The text is the default; the head may pass localised steps instead.</summary>
    public static IReadOnlyList<CoachStep> Default { get; } =
    [
        new(CoachGesture.Swipe, "Swipe between chats", "Swipe left or right to move to the next or previous chat. The edge of the neighbour shows where it is."),
        new(CoachGesture.Hold, "Hold for the menu", "Hold empty space for the radial menu, with dictation at the centre. Hold the message box to dictate."),
    ];
}
