namespace Bazaar.Client.Gestures;

/// <summary>
/// A back-gesture source the platform head drives by hand: the Android head forwards <c>OnBackInvokedCallback</c> progress into it
/// (doc 93; whether Avalonia 12's head exposes the callback is unverified, so the head may need to own the callback). Platform-neutral,
/// so tests and other heads use it too.
/// </summary>
public sealed class BackGestureRelay : IBackGestureSource
{
    public bool IsPredictive => true;

    public event EventHandler<BackGestureEventArgs>? Started;

    public event EventHandler<BackGestureEventArgs>? Progressed;

    public event EventHandler<BackGestureEventArgs>? Committed;

    public event EventHandler<BackGestureEventArgs>? Cancelled;

    public void Start(double progress, BackGestureEdge edge) => Started?.Invoke(this, new BackGestureEventArgs(progress, edge));

    public void Progress(double progress, BackGestureEdge edge) => Progressed?.Invoke(this, new BackGestureEventArgs(progress, edge));

    public void Commit(double progress, BackGestureEdge edge) => Committed?.Invoke(this, new BackGestureEventArgs(progress, edge));

    public void Cancel(double progress, BackGestureEdge edge) => Cancelled?.Invoke(this, new BackGestureEventArgs(progress, edge));
}

/// <summary>
/// The fallback where live progress is not available (below Android 14, desktop, or a head that cannot forward the callback):
/// back is an instant pop. The head calls <see cref="Back"/> from the plain back handler. Only <see cref="Committed"/> is ever raised.
/// </summary>
public sealed class FallbackBackGestureSource : IBackGestureSource
{
    public bool IsPredictive => false;

    // Never raised: there is no progress without the platform callback.
    public event EventHandler<BackGestureEventArgs>? Started
    {
        add { }
        remove { }
    }

    public event EventHandler<BackGestureEventArgs>? Progressed
    {
        add { }
        remove { }
    }

    public event EventHandler<BackGestureEventArgs>? Cancelled
    {
        add { }
        remove { }
    }

    public event EventHandler<BackGestureEventArgs>? Committed;

    /// <summary>Back was invoked: pop now.</summary>
    public void Back() => Committed?.Invoke(this, new BackGestureEventArgs(1, BackGestureEdge.Left));
}
