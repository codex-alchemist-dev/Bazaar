namespace Bazaar.Client.Posture;

/// <summary>
/// A posture source driven by hand, for tests and for heads without a fold sensor. The Android head replaces it with a
/// WindowManager-backed source; nothing else changes.
/// </summary>
public sealed class FakePostureSource : IPostureSource
{
    public FakePostureSource(PostureState? initial = null) => Current = initial ?? PostureState.Flat;

    public PostureState Current { get; private set; }

    public event EventHandler<PostureState>? Changed;

    /// <summary>Sets the reading and raises <see cref="Changed"/>.</summary>
    public void Set(PostureState state)
    {
        Current = state;
        Changed?.Invoke(this, state);
    }
}
