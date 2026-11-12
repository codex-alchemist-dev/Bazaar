namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Drops frames that are not newer than the last one applied, ordered by (GraphVersion, LayoutSeq).
/// Frames can arrive out of order after a reconnect; this keeps the view from moving backwards.
/// </summary>
public sealed class FrameSequencer
{
    private (long GraphVersion, long LayoutSeq)? _latest;

    /// <summary>Accepts the frame if it is strictly newer than the newest one accepted so far.</summary>
    public bool TryAccept(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_latest is { } latest && !IsNewer(frame, latest))
        {
            return false;
        }

        _latest = (frame.GraphVersion, frame.LayoutSeq);
        return true;
    }

    /// <summary>Forgets the newest frame. Call when the client reconnects and requests a fresh snapshot.</summary>
    public void Reset() => _latest = null;

    private static bool IsNewer(RenderFrame frame, (long GraphVersion, long LayoutSeq) latest) =>
        frame.GraphVersion != latest.GraphVersion
            ? frame.GraphVersion > latest.GraphVersion
            : frame.LayoutSeq > latest.LayoutSeq;
}
