namespace Bazaar.Client.PhoneCoach;

/// <summary>Builds <see cref="ChatStripState"/> from the pager's page names. Pure, so the strip's content is tested without a view.</summary>
public static class ChatStrip
{
    /// <summary>The state for pages named <paramref name="names"/> with <paramref name="selected"/> on screen. An out-of-range index selects nothing.</summary>
    public static ChatStripState From(IReadOnlyList<string> names, int selected)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0 ? ChatStripState.Empty : new ChatStripState(names, selected >= 0 && selected < names.Count ? selected : -1);
    }
}
