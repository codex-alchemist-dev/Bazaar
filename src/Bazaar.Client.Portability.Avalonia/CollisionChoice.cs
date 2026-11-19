using Bazaar.Client.Portability;

namespace Bazaar.Client.Portability.Avalonia;

/// <summary>
/// One colliding record in the preview with its per-item choice (doc 44 section 8.10). Keep both is the default.
/// A change is passed to <paramref name="onChoose"/>, which applies it to the wizard state.
/// </summary>
public sealed class CollisionChoice(PreviewCollision collision, string sourceKey, CollisionAction action, Action<CollisionChoice, CollisionAction> onChoose)
{
    private CollisionAction _action = action;

    /// <summary>The labels offered in the choice box, in the order of <see cref="CollisionAction"/>.</summary>
    public static IReadOnlyList<string> Labels { get; } = ["Keep both (flagged)", "Skip incoming", "Replace (audited)"];

    public string Target => collision.Target;

    public string Title => collision.Title;

    public string SourceKey => sourceKey;

    /// <summary>The label of the current choice. Setting it applies the matching action.</summary>
    public string SelectedLabel
    {
        get => Labels[(int)_action];
        set
        {
            var index = Labels.ToList().IndexOf(value);
            if (index < 0 || (CollisionAction)index == _action)
            {
                return;
            }
            _action = (CollisionAction)index;
            onChoose(this, _action);
        }
    }
}
