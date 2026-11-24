namespace Bazaar.Client.PhoneCoach;

/// <summary>
/// Builds the pager's custom accessibility actions (doc 93: Next chat, Previous chat, Open menu) from the navigator, so each gesture has a
/// non-gesture route for TalkBack and other assistive tech. Rebuild the list after the page changes so the enabled flags stay true.
/// </summary>
public static class PagerAccessibility
{
    /// <summary>The three actions in a fixed order, with <see cref="PagerAction.Enabled"/> reflecting the current page.</summary>
    public static IReadOnlyList<PagerAction> Build(IPagerNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        int selected = navigator.SelectedPage;
        bool hasPages = selected >= 0;
        return
        [
            new(PagerActionIds.NextChat, "Next chat", navigator.GoToNext, hasPages && selected + 1 < navigator.PageCount),
            new(PagerActionIds.PreviousChat, "Previous chat", navigator.GoToPrevious, hasPages && selected > 0),
            new(PagerActionIds.OpenMenu, "Open menu", navigator.OpenMenu, true),
        ];
    }
}
