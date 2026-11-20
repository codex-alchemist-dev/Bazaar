namespace Bazaar.Client.Core.Commands;

/// <summary>
/// Session-local list of recently run command ids, most recent first. Held in memory only, never persisted.
/// Inject one instance into <see cref="CommandMatcher"/>; the matcher reads it, the caller calls <see cref="Touch"/>.
/// </summary>
public sealed class CommandRecency
{
    private readonly List<string> _ids = [];

    public CommandRecency(int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Ids, most recent first.</summary>
    public IReadOnlyList<string> Ids => _ids.ToArray();

    /// <summary>Moves the id to the front, removing any earlier entry, and trims to capacity.</summary>
    public void Touch(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _ids.Remove(id);
        _ids.Insert(0, id);
        if (_ids.Count > Capacity)
        {
            _ids.RemoveRange(Capacity, _ids.Count - Capacity);
        }
    }

    /// <summary>Zero-based rank (0 is most recent), or -1 when the id is not recorded.</summary>
    public int Rank(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _ids.IndexOf(id);
    }

    public void Clear() => _ids.Clear();
}
