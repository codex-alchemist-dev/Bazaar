using System.Collections.ObjectModel;
using Bazaar.Client.Chat.Abstractions;

namespace Bazaar.Client.Chat;

/// <summary>An observable list of one message's reactions, edited in place so a view can tell which chip joined, left or changed.</summary>
public sealed class ReactionList : ObservableCollection<ChatReaction>, IReactionCollection
{
    /// <summary>Makes the list equal <paramref name="target"/> with the fewest edits: gone chips leave, changed chips are replaced, new chips join.</summary>
    public void SyncTo(IReadOnlyList<ChatReaction> target)
    {
        var wanted = target.Where(r => r.Count > 0).ToList();
        for (int i = Count - 1; i >= 0; i--)
        {
            if (wanted.All(r => r.Emoji != this[i].Emoji)) RemoveAt(i);
        }

        foreach (ChatReaction reaction in wanted)
        {
            int at = IndexOfEmoji(reaction.Emoji);
            if (at < 0) Add(reaction);
            else if (this[at] != reaction) this[at] = reaction;
        }
    }

    private int IndexOfEmoji(string emoji)
    {
        for (int i = 0; i < Count; i++)
        {
            if (this[i].Emoji == emoji) return i;
        }

        return -1;
    }
}

/// <summary>A reaction source held in memory. A head feeds it from the wire; tests use it as the fake source.</summary>
public sealed class InMemoryChatReactions : IChatReactionSource
{
    private readonly Dictionary<long, List<ChatReaction>> _byMessage = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public event EventHandler<long>? Changed;

    /// <inheritdoc />
    public IReadOnlyList<ChatReaction> Reactions(long n)
    {
        lock (_gate) return _byMessage.TryGetValue(n, out var list) ? [.. list] : [];
    }

    /// <summary>Sets the count (and own flag) of one chip on a message. A count of zero removes the chip.</summary>
    public void Set(long n, string emoji, int count, bool mine = false)
    {
        lock (_gate)
        {
            if (!_byMessage.TryGetValue(n, out var list)) _byMessage[n] = list = [];
            int at = list.FindIndex(r => r.Emoji == emoji);
            if (count <= 0)
            {
                if (at >= 0) list.RemoveAt(at);
            }
            else if (at >= 0) list[at] = new ChatReaction(emoji, count, mine);
            else list.Add(new ChatReaction(emoji, count, mine));
        }

        Changed?.Invoke(this, n);
    }
}

/// <summary>A typing source set by hand. A head feeds it from the wire; tests use it as the fake source.</summary>
public sealed class ManualTypingSource : IChatTypingSource
{
    private IReadOnlyList<string> _typers = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Typers => _typers;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Replaces the members typing now.</summary>
    public void Set(params string[] names)
    {
        _typers = names;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
