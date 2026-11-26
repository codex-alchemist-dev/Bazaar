namespace Bazaar.Client.PhoneCoach;

/// <summary>The stable ids of the pager's custom accessibility actions (doc 93: Next chat, Previous chat, Open menu).</summary>
public static class PagerActionIds
{
    public const string NextChat = "bazaar.pager.next";
    public const string PreviousChat = "bazaar.pager.previous";
    public const string OpenMenu = "bazaar.pager.menu";
}

/// <summary>
/// A custom accessibility action on the pager. Gestures are never the only route, so every swipe and hold has one of these.
/// The platform head maps each to a TalkBack custom action (Android: <c>AccessibilityNodeInfo.AccessibilityAction</c>) using
/// <see cref="Label"/> as the spoken name, and calls <see cref="Invoke"/> when the person picks it.
/// </summary>
/// <param name="Id">One of <see cref="PagerActionIds"/>.</param>
/// <param name="Label">The spoken name, for example "Next chat".</param>
/// <param name="Invoke">Performs the action; returns false when it could not (no next chat).</param>
/// <param name="Enabled">False when the action does nothing right now (the last chat has no next); the head hides or greys it.</param>
public sealed record PagerAction(string Id, string Label, Func<bool> Invoke, bool Enabled);

/// <summary>What the pager exposes for the accessibility actions to drive. <c>PhoneShell</c> implements it.</summary>
public interface IPagerNavigator
{
    /// <summary>The page on screen, or -1 when there are none.</summary>
    int SelectedPage { get; }

    /// <summary>How many pages there are.</summary>
    int PageCount { get; }

    /// <summary>Shows the next page; false on the last.</summary>
    bool GoToNext();

    /// <summary>Shows the previous page; false on the first.</summary>
    bool GoToPrevious();

    /// <summary>Opens the menu a hold would open (the radial sectors as a standard action list under TalkBack); false when it cannot.</summary>
    bool OpenMenu();
}
