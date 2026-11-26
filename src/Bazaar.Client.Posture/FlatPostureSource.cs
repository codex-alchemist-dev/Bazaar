namespace Bazaar.Client.Posture;

/// <summary>
/// The posture source of a head with no fold sensor: the Windows and Linux desktop heads. It always reads
/// <see cref="PostureState.Flat"/> and never raises <see cref="Changed"/>; the layout then follows the window width alone
/// (dual-pane from the threshold, single pane below it).
/// </summary>
public sealed class FlatPostureSource : IPostureSource
{
    public PostureState Current => PostureState.Flat;

    public event EventHandler<PostureState>? Changed
    {
        add { }
        remove { }
    }
}
