namespace Bazaar.Portability.Client.Avalonia;

/// <summary>
/// One collision in the preview with the per-item choice: which source record to keep.
/// The choice is held here for the view; the wizard state machine has no transition that applies it yet.
/// </summary>
public sealed class CollisionChoice(PreviewCollision collision)
{
    public string Target => collision.Target;

    public string Title => collision.Title;

    public IReadOnlyList<string> SourceKeys => collision.SourceKeys;

    /// <summary>The source key the user keeps. Starts on the first source key.</summary>
    public string? SelectedKey { get; set; } = collision.SourceKeys.Count > 0 ? collision.SourceKeys[0] : null;
}
