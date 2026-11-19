namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// Test double for <see cref="IPostureSource"/>. The test pushes fold snapshots; the fake maps them with
/// <see cref="FoldPosture.Map(FoldSnapshot)"/> and emits only real changes, the same contract as the platform source.
/// </summary>
public sealed class FakePostureSource : IPostureSource
{
    private readonly List<IObserver<PostureKind>> _observers = [];
    private bool _disposed;

    public FakePostureSource(FoldSnapshot? initial = null)
    {
        Current = FoldPosture.Map(initial ?? FoldSnapshot.NoFold);
    }

    public PostureKind Current { get; private set; }

    public IObservable<PostureKind> Changes => new Subscribable(this);

    /// <summary>Feed a new fold snapshot, as the platform would after a fold, unfold or hinge change.</summary>
    public void Push(FoldSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = FoldPosture.Map(snapshot);
        if (next == Current)
        {
            return;
        }

        Current = next;
        foreach (var observer in _observers.ToArray())
        {
            observer.OnNext(next);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var observer in _observers.ToArray())
        {
            observer.OnCompleted();
        }

        _observers.Clear();
    }

    private sealed class Subscribable(FakePostureSource owner) : IObservable<PostureKind>
    {
        public IDisposable Subscribe(IObserver<PostureKind> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            owner._observers.Add(observer);
            return new Unsubscriber(owner._observers, observer);
        }
    }

    private sealed class Unsubscriber(List<IObserver<PostureKind>> observers, IObserver<PostureKind> observer) : IDisposable
    {
        public void Dispose() => observers.Remove(observer);
    }
}
