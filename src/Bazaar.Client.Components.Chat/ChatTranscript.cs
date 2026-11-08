using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Components.Chat;

/// <summary>
/// One group chat's messages, oldest first, one entry per log sequence number. An edit repeats the original <c>n</c> with a
/// higher <c>ev</c>, so the highest version wins (doc 02, sequence numbers). Events for other group chats are ignored.
/// </summary>
public sealed class ChatTranscript(Guid gcId)
{
    private readonly SortedDictionary<long, WireGcMessage> _entries = [];

    public Guid GcId { get; } = gcId;

    /// <summary>Messages in log order.</summary>
    public IReadOnlyList<WireGcMessage> Items => [.. _entries.Values];

    /// <summary>Applies one event. Returns true when the transcript changed.</summary>
    public bool Apply(WireGcMessage message)
    {
        if (message.GcId != GcId)
        {
            return false;
        }

        if (_entries.TryGetValue(message.N, out var current) && current.Ev >= message.Ev)
        {
            return false;
        }

        _entries[message.N] = message;
        return true;
    }

    /// <summary>Applies several events, oldest first. Returns how many changed the transcript.</summary>
    public int ApplyAll(IEnumerable<WireGcMessage> messages) => messages.Count(Apply);
}
