using System.Globalization;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Chat;

/// <summary>
/// The messages of one group chat as the client holds them. Each sequence number keeps its newest version (higher
/// <c>ev</c> wins), and the lines come out in sequence order. Thread-safe; no I/O.
/// </summary>
/// <param name="members">Display names by principal id. Unknown authors show the first eight characters of their id.</param>
/// <param name="ownName">Label for messages this client sent.</param>
/// <param name="zone">Time zone for the header's clock time.</param>
public sealed class ChatTimeline(IReadOnlyDictionary<Guid, ChatMember> members, string ownName, TimeZoneInfo zone)
{
    private readonly IReadOnlyDictionary<Guid, ChatMember> _members = members ?? throw new ArgumentNullException(nameof(members));
    private readonly string _ownName = ownName ?? throw new ArgumentNullException(nameof(ownName));
    private readonly TimeZoneInfo _zone = zone ?? throw new ArgumentNullException(nameof(zone));
    private readonly Dictionary<long, WireGcMessage> _messages = [];
    private readonly HashSet<long> _own = [];
    private readonly Lock _gate = new();

    /// <summary>Applies one message version. Returns false when it is older than or the same as the held version.</summary>
    public bool Apply(WireGcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (_messages.TryGetValue(message.N, out WireGcMessage? held) && held.Ev >= message.Ev) return false;
            _messages[message.N] = message;
            return true;
        }
    }

    /// <summary>Marks a sequence number as sent by this client, so it shows under the own label.</summary>
    public void MarkOwn(long n)
    {
        lock (_gate) _own.Add(n);
    }

    /// <summary>The current version of every message, oldest first.</summary>
    public IReadOnlyList<ChatLine> Lines()
    {
        lock (_gate)
        {
            return _messages.Values
                .OrderBy(m => m.N)
                .Select(m => ToLine(m, _own.Contains(m.N)))
                .ToList();
        }
    }

    private ChatLine ToLine(WireGcMessage message, bool own)
    {
        string author;
        if (own) author = _ownName;
        else if (_members.TryGetValue(message.AuthorId, out ChatMember? known)) author = known.Label;
        else author = message.AuthorId.ToString("N", CultureInfo.InvariantCulture)[..8];

        string time = TimeZoneInfo.ConvertTime(message.Ts, _zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        return new ChatLine(message.N, message.AuthorId, author, time, message.Text, message.Edited, own);
    }
}
