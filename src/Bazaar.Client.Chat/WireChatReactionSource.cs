using Bazaar.Client.Chat.Abstractions;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Chat;

/// <summary>
/// Reaction state of one group chat, fed from the wire (docs/design/02, "Reactions on the wire"): a snapshot from
/// <c>gc.reactions</c> and live <c>gc.reaction</c> events. Updates are set operations, so applying an event twice, or an event the
/// snapshot already holds, changes nothing. Thread-safe; <see cref="Changed"/> is raised on the calling thread, outside the lock.
/// </summary>
/// <param name="gcId">The group chat; events of other chats are ignored.</param>
/// <param name="self">This client's principal id, or null while it is not known yet. It decides which chips are <see cref="ChatReaction.Mine"/>.</param>
public sealed class WireChatReactionSource(Guid gcId, Func<Guid?> self) : IChatReactionSource
{
    private readonly Dictionary<long, List<(string Emoji, List<Guid> By)>> _byMessage = [];
    private readonly List<WireGcReaction> _buffered = [];
    private readonly Lock _gate = new();
    private bool _loading;

    /// <inheritdoc />
    public event EventHandler<long>? Changed;

    /// <summary>True between <see cref="BeginLoad"/> and the <see cref="Load"/> or <see cref="AbortLoad"/> that ends it.</summary>
    public bool IsLoading
    {
        get
        {
            lock (_gate) return _loading;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ChatReaction> Reactions(long n)
    {
        var me = self();
        lock (_gate)
        {
            return _byMessage.TryGetValue(n, out var groups)
                ? [.. groups.Select(g => new ChatReaction(g.Emoji, g.By.Count, me is { } id && g.By.Contains(id)))]
                : [];
        }
    }

    /// <summary>Applies one live event. While a snapshot is loading the event is held and applied after it, in arrival order.</summary>
    public void Apply(WireGcReaction reaction)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        if (reaction.GcId != gcId)
            return;

        bool changed;
        lock (_gate)
        {
            if (_loading)
            {
                _buffered.Add(reaction);
                return;
            }

            changed = ApplyLocked(reaction);
        }

        if (changed)
            Changed?.Invoke(this, reaction.TargetN);
    }

    /// <summary>Starts a snapshot load: events that arrive until <see cref="Load"/> are held, so none is lost between the request and its answer.</summary>
    public void BeginLoad()
    {
        lock (_gate) _loading = true;
    }

    /// <summary>Replaces the state with the snapshot, then applies the events held since <see cref="BeginLoad"/>.</summary>
    public void Load(GcReactionsReply snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<long> touched = [];
        lock (_gate)
        {
            touched.UnionWith(_byMessage.Keys);
            _byMessage.Clear();
            foreach (var group in snapshot.Items)
            {
                if (group.By.Count == 0)
                    continue;
                if (!_byMessage.TryGetValue(group.TargetN, out var groups))
                    _byMessage[group.TargetN] = groups = [];
                groups.Add((group.Emoji, [.. group.By]));
                touched.Add(group.TargetN);
            }

            foreach (var reaction in _buffered)
            {
                if (ApplyLocked(reaction))
                    touched.Add(reaction.TargetN);
            }

            _buffered.Clear();
            _loading = false;
        }

        foreach (var n in touched)
            Changed?.Invoke(this, n);
    }

    /// <summary>Ends a load that failed, applying the held events to the state as it was.</summary>
    public void AbortLoad()
    {
        List<long> touched = [];
        lock (_gate)
        {
            foreach (var reaction in _buffered)
            {
                if (ApplyLocked(reaction))
                    touched.Add(reaction.TargetN);
            }

            _buffered.Clear();
            _loading = false;
        }

        foreach (var n in touched)
            Changed?.Invoke(this, n);
    }

    private bool ApplyLocked(WireGcReaction reaction)
    {
        if (!_byMessage.TryGetValue(reaction.TargetN, out var groups))
        {
            if (!reaction.Add)
                return false;
            _byMessage[reaction.TargetN] = groups = [];
        }

        var at = groups.FindIndex(g => g.Emoji == reaction.Emoji);
        if (reaction.Add)
        {
            if (at < 0)
            {
                groups.Add((reaction.Emoji, [reaction.By]));
                return true;
            }

            if (groups[at].By.Contains(reaction.By))
                return false;
            groups[at].By.Add(reaction.By);
            return true;
        }

        if (at < 0 || !groups[at].By.Remove(reaction.By))
            return false;
        if (groups[at].By.Count == 0)
            groups.RemoveAt(at);
        if (groups.Count == 0)
            _byMessage.Remove(reaction.TargetN);
        return true;
    }
}
