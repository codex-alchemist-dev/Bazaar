using Avalonia;
using Avalonia.Controls;

namespace Bazaar.Client.Core.Souls;

/// <summary>
/// Profile card and version history for one Soul. Set <see cref="Control.DataContext"/> to a
/// <see cref="SoulProfileViewModel"/>; the control only renders it.
/// </summary>
public partial class SoulProfileView : UserControl
{
    /// <summary>Spacing used when the theme does not supply a space token.</summary>
    private const double _fallbackSpace = 8;

    /// <summary>Creates the view. Bindings are compiled against <see cref="SoulProfileViewModel"/>.</summary>
    public SoulProfileView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ApplySpacing();
    }

    /// <summary>Applies the theme's space tokens to padding and margins, which XAML cannot bind from a double.</summary>
    private void ApplySpacing()
    {
        double md = SpaceToken("Bazaar.space.md");
        double xs = SpaceToken("Bazaar.space.xs");

        Root.Margin = new Thickness(md);
        CardBorder.Padding = new Thickness(md);
        ModelBadge.Padding = new Thickness(xs);
    }

    private double SpaceToken(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is double length
            ? length
            : _fallbackSpace;

    // TODO(component): mount this card from the GC head avatar and the agent roster once those views exist
    // (docs/design/27-client-architecture.md, GC message anatomy). Not wired to MainWindow in this change.
}
