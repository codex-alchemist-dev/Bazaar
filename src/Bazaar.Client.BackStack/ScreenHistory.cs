using Bazaar.Client.BackStack.Abstractions;

namespace Bazaar.Client.BackStack;

/// <summary>The default <see cref="IScreenHistory"/>. Single-threaded: call it from the UI thread, as the shell does.</summary>
public sealed class ScreenHistory : IScreenHistory
{
    private readonly List<ScreenEntry> _entries;

    public ScreenHistory(ScreenEntry root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root.Kind);
        _entries = [root];
    }

    public ScreenEntry Root => _entries[0];

    public ScreenEntry Current => _entries[^1];

    public ScreenEntry? Behind => _entries.Count > 1 ? _entries[^2] : null;

    public int Depth => _entries.Count;

    public bool CanPop => _entries.Count > 1;

    public IReadOnlyList<ScreenEntry> Entries => _entries.ToArray();

    public event EventHandler<ScreenHistoryChangedEventArgs>? Changed;

    public bool Push(ScreenEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Kind);
        if (entry == Current)
        {
            return false;
        }

        _entries.Add(entry);
        Raise(ScreenHistoryChangeKind.Pushed, entry);
        return true;
    }

    public bool Pop()
    {
        if (!CanPop)
        {
            return false;
        }

        var removed = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        Raise(ScreenHistoryChangeKind.Popped, removed);
        return true;
    }

    public bool PopTo(string kind)
    {
        var target = _entries.FindLastIndex(e => e.Kind == kind);
        if (target < 0 || target == _entries.Count - 1)
        {
            return false;
        }

        var removed = _entries[^1];
        _entries.RemoveRange(target + 1, _entries.Count - target - 1);
        Raise(ScreenHistoryChangeKind.Popped, removed);
        return true;
    }

    public bool ReplaceTop(ScreenEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Kind);
        if (!CanPop || entry == Current)
        {
            return false;
        }

        var replaced = _entries[^1];
        _entries[^1] = entry;
        Raise(ScreenHistoryChangeKind.Replaced, replaced);
        return true;
    }

    public bool PopToRoot()
    {
        if (!CanPop)
        {
            return false;
        }

        var removed = _entries[^1];
        _entries.RemoveRange(1, _entries.Count - 1);
        Raise(ScreenHistoryChangeKind.Reset, removed);
        return true;
    }

    private void Raise(ScreenHistoryChangeKind change, ScreenEntry affected) => Changed?.Invoke(this, new ScreenHistoryChangedEventArgs(change, affected));
}
