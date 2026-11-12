namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Drops frames that are not newer than the last one applied, ordered by (Epoch, Sequence).
/// Frames can arrive out of order after a reconnect; this keeps the view from moving backwards.
/// </summary>
public sealed class FrameSequencer
{
    private (long Epoch, long Sequence)? _latest;

    /// <summary>Accepts the frame if it is strictly newer than the newest one accepted so far.</summary>
    public bool TryAccept(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_latest is { } latest && !IsNewer(frame, latest))
        {
            return false;
        }

        _latest = (frame.Epoch, frame.Sequence);
        return true;
    }

    /// <summary>Forgets the newest frame. Call when the client reconnects and requests a fresh snapshot.</summary>
    public void Reset() => _latest = null;

    private static bool IsNewer(RenderFrame frame, (long Epoch, long Sequence) latest) =>
        frame.Epoch != latest.Epoch
            ? frame.Epoch > latest.Epoch
            : frame.Sequence > latest.Sequence;
}
