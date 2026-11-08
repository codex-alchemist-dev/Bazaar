using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// The phone's root view. It shows the server layout variant the posture selects. Pane contents arrive with
/// the server profile (doc 29); this shell only switches the variant. TODO(android-shell): folded pager and
/// unfolded panes replace the placeholder text.
/// </summary>
public sealed class PhoneShell : UserControl
{
    private readonly TextBlock _variantLabel = new()
    {
        FontSize = 16,
        Margin = new Thickness(16),
        TextWrapping = TextWrapping.Wrap,
    };

    public PhoneShell()
    {
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Bazaar", FontSize = 22, Margin = new Thickness(16, 16, 16, 0), FontWeight = FontWeight.SemiBold },
                _variantLabel,
            },
        };

        Layout = LayoutVariant.Folded;
    }

    public LayoutVariant Layout
    {
        get;
        private set
        {
            field = value;
            _variantLabel.Text = value == LayoutVariant.Unfolded ? "Unfolded layout" : "Folded layout";
        }
    } = LayoutVariant.Folded;

    /// <summary>Switches variants locally when the posture or width changes. No round trip (doc 29).</summary>
    public LayoutVariant ApplyPosture(PostureKind posture, HalfOpenLayoutPolicy halfOpen, double widthDp)
    {
        Layout = PostureLayout.Select(posture, halfOpen, widthDp);
        return Layout;
    }
}
