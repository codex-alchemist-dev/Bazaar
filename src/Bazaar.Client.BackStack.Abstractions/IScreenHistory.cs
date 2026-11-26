namespace Bazaar.Client.BackStack.Abstractions;

/// <summary>
/// One screen on the phone back stack (doc 93, "Predictive back"): a kind such as <c>chat</c>, <c>thread</c> or <c>profile</c>, and an
/// optional key that says which one (a GC id, a thread id, a person id). The stack holds no views; the shell maps an entry to a view.
/// </summary>
public readonly record struct ScreenEntry(string Kind, string? Key = null);

/// <summary>What changed on the stack.</summary>
public enum ScreenHistoryChangeKind
{
    Pushed,
    Popped,
    Replaced,
    Reset,
}

/// <summary>The kind of change and the entry that was added or removed.</summary>
public sealed class ScreenHistoryChangedEventArgs : EventArgs
{
    public ScreenHistoryChangedEventArgs(ScreenHistoryChangeKind change, ScreenEntry affected)
    {
        Change = change;
        Affected = affected;
    }

    public ScreenHistoryChangeKind Change { get; }

    /// <summary>The entry pushed, or the entry that was popped, replaced away or dropped by a reset.</summary>
    public ScreenEntry Affected { get; }
}

/// <summary>
/// The phone's back stack: a root screen that is never popped (the chat pager) and the screens pushed over it. Pure logic, no UI.
/// Back pops one screen; at the root there is nothing to pop and the system handles back (leaving the app).
/// </summary>
public interface IScreenHistory
{
    /// <summary>The root screen, always present.</summary>
    ScreenEntry Root { get; }

    /// <summary>The screen on top.</summary>
    ScreenEntry Current { get; }

    /// <summary>The screen beneath <see cref="Current"/> (the one a back swipe reveals), or null when only the root is left.</summary>
    ScreenEntry? Behind { get; }

    /// <summary>Number of screens including the root; at least 1.</summary>
    int Depth { get; }

    /// <summary>True when a back would pop a screen (depth above 1).</summary>
    bool CanPop { get; }

    /// <summary>A snapshot of the screens from the root to the top.</summary>
    IReadOnlyList<ScreenEntry> Entries { get; }

    /// <summary>Raised after every change, on the calling thread.</summary>
    event EventHandler<ScreenHistoryChangedEventArgs>? Changed;

    /// <summary>Puts a screen on top. Pushing the entry already on top does nothing and returns false.</summary>
    bool Push(ScreenEntry entry);

    /// <summary>Removes the top screen. Returns false at the root, where there is nothing to pop.</summary>
    bool Pop();

    /// <summary>Pops down to the nearest screen of <paramref name="kind"/> below the top and keeps it; false when none exists (nothing changes).</summary>
    bool PopTo(string kind);

    /// <summary>Swaps the top screen without growing the stack. At the root this is a no-op that returns false.</summary>
    bool ReplaceTop(ScreenEntry entry);

    /// <summary>Drops everything above the root. Returns false when already there.</summary>
    bool PopToRoot();
}
