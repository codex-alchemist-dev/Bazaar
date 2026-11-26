namespace Bazaar.Client.Gestures;

/// <summary>The screen edge a back swipe starts from.</summary>
public enum BackGestureEdge
{
    Left,
    Right,
}

/// <summary>One step of a back gesture: progress from 0 (finger down) to 1 (fully swiped) and the edge it started from.</summary>
public sealed class BackGestureEventArgs : EventArgs
{
    public BackGestureEventArgs(double progress, BackGestureEdge edge)
    {
        Progress = Math.Clamp(progress, 0.0, 1.0);
        Edge = edge;
    }

    public double Progress { get; }

    public BackGestureEdge Edge { get; }
}

/// <summary>
/// Where back-gesture progress comes from (doc 93, "Predictive back"; same pattern as <c>IPostureSource</c>). The Android head implements it
/// over <c>OnBackInvokedCallback</c> when that works, and <see cref="IsPredictive"/> is true then. Where it does not (below Android 14, or when
/// the Avalonia head does not expose the callback; unverified) a fallback source raises only <see cref="Committed"/>, which is an instant pop.
/// Nothing in Core or the views depends on the Android API itself.
/// </summary>
public interface IBackGestureSource
{
    /// <summary>True when <see cref="Progressed"/> carries live progress; false means back arrives only as <see cref="Committed"/>.</summary>
    bool IsPredictive { get; }

    /// <summary>The system started a back gesture.</summary>
    event EventHandler<BackGestureEventArgs>? Started;

    /// <summary>The finger moved; <see cref="BackGestureEventArgs.Progress"/> is the new progress.</summary>
    event EventHandler<BackGestureEventArgs>? Progressed;

    /// <summary>The gesture was released past the commit point (or back was invoked without progress): pop the screen.</summary>
    event EventHandler<BackGestureEventArgs>? Committed;

    /// <summary>The gesture was cancelled: the screen springs back.</summary>
    event EventHandler<BackGestureEventArgs>? Cancelled;
}
