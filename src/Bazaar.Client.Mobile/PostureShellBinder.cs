namespace Bazaar.Client.Mobile;

/// <summary>
/// Connects an <see cref="IPostureSource"/> to a <see cref="PhoneShell"/>. It applies the current posture at once and
/// again on every change, so the shell switches variants locally with no round trip (doc 29, WP3). The width comes
/// from the head, because the half-open rule depends on it.
/// </summary>
public sealed class PostureShellBinder : IDisposable
{
    private readonly PhoneShell _shell;
    private readonly Func<double> _widthDp;
    private readonly HalfOpenLayoutPolicy _halfOpen;
    private readonly IDisposable _subscription;

    public PostureShellBinder(IPostureSource source, PhoneShell shell, Func<double> widthDp, HalfOpenLayoutPolicy halfOpen)
    {
        ArgumentNullException.ThrowIfNull(source);
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _widthDp = widthDp ?? throw new ArgumentNullException(nameof(widthDp));
        _halfOpen = halfOpen;

        Apply(source.Current);
        _subscription = source.Changes.Subscribe(new Observer(this));
    }

    /// <summary>The variant the shell shows after the most recent posture.</summary>
    public LayoutVariant Layout { get; private set; }

    public void Dispose() => _subscription.Dispose();

    /// <summary>Applies one posture. Public so a head can re-apply after a width change, such as a window resize.</summary>
    public void Apply(PostureKind posture) =>
        Layout = _shell.ApplyPosture(posture, _halfOpen, _widthDp());

    private sealed class Observer(PostureShellBinder owner) : IObserver<PostureKind>
    {
        public void OnNext(PostureKind value) => owner.Apply(value);

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}
