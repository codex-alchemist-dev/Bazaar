namespace Bazaar.Portability.Client.Avalonia;

/// <summary>
/// One collision in the preview with the per-item choice: which source record to keep. A null selection keeps every
/// record and flags the collision. A change is passed to <paramref name="onChoose"/>, which applies it to the wizard.
/// </summary>
public sealed class CollisionChoice(PreviewCollision collision, string? keepKey, Action<CollisionChoice, string> onChoose)
{
    private string? _selectedKey = keepKey;

    public string Target => collision.Target;

    public string Title => collision.Title;

    public IReadOnlyList<string> SourceKeys => collision.SourceKeys;

    /// <summary>The source key the user keeps. Null means keep every record.</summary>
    public string? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (string.Equals(_selectedKey, value, StringComparison.Ordinal) || value is null)
            {
                _selectedKey = value;
                return;
            }
            _selectedKey = value;
            onChoose(this, value);
        }
    }
}
