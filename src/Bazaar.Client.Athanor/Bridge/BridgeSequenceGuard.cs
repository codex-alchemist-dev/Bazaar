namespace Bazaar.Client.Athanor.Bridge;

/// <summary>
/// Tracks the last <c>seq</c> the editor sent and refuses anything that does not rise (doc 57 §4). One guard per editor
/// connection. It is not thread-safe: the host reads the bridge on one thread.
/// </summary>
public sealed class BridgeSequenceGuard
{
    private long _lastSeq;

    /// <summary>The highest <c>seq</c> accepted so far, or 0 when none has been.</summary>
    public long LastSeq => _lastSeq;

    /// <summary>Accepts <paramref name="seq"/> only when it is above every accepted value. A refused value leaves the guard unchanged.</summary>
    public bool TryAccept(long seq)
    {
        if (seq <= _lastSeq)
        {
            return false;
        }

        _lastSeq = seq;
        return true;
    }
}
