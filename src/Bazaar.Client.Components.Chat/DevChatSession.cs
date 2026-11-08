using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Components.Chat;

/// <summary>
/// The dev chat: one configured group chat, its history, live messages (including replies from the agents) and sending.
/// The group chat id comes from configuration; nothing here names a GC or an agent.
/// </summary>
public sealed class DevChatSession
{
    public const string StreamPrefix = "gc:";

    private readonly IGcWire _wire;
    private readonly int _historyPageSize;

    public DevChatSession(IGcWire wire, Guid gcId, int historyPageSize = 50)
    {
        if (historyPageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(historyPageSize), "History pages hold at least one entry.");
        }

        _wire = wire;
        GcId = gcId;
        _historyPageSize = historyPageSize;
        Transcript = new ChatTranscript(gcId);
    }

    public ChatTranscript Transcript { get; }

    public Guid GcId { get; }

    /// <summary>Loads the newest page of history, then subscribes to live events for the chat.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        var page = await _wire.HistoryAsync(new GcHistory(GcId, long.MaxValue, _historyPageSize), cancellationToken);
        Transcript.ApplyAll(page.Items);
        await _wire.SubscribeAsync(StreamPrefix + GcId.ToString("D"), cancellationToken);
    }

    /// <summary>Reads live events until the wire channel completes or the token is cancelled.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (await _wire.Messages.WaitToReadAsync(cancellationToken))
        {
            while (_wire.Messages.TryRead(out var message))
            {
                Transcript.Apply(message);
            }
        }
    }

    /// <summary>Sends text to the chat. Blank text is rejected before it reaches the server.</summary>
    public Task<GcSendResult> SendAsync(string text, long? replyToN, CancellationToken cancellationToken)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A message needs text.", nameof(text));
        }

        return _wire.SendAsync(new GcSend(GcId, trimmed, null, replyToN), cancellationToken);
    }
}
