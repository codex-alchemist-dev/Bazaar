using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Mobile;

/// <summary>
/// Draws the radial menu around the point where the press began: a centre disc labelled for dictation and one label per sector,
/// with the sector under the finger highlighted. It draws only; <see cref="RadialMenu"/> decides what a release does.
/// </summary>
public sealed class RadialMenuView : Canvas
{
    public const string CentreLabel = "Dictate";

    /// <summary>The theme resource key for the sector and centre surface (doc 93: views name tokens, never literals).</summary>
    public const string SurfaceToken = "Bazaar.Radial.Surface";

    /// <summary>The theme resource key for the label text.</summary>
    public const string TextToken = "Bazaar.Radial.Text";

    // Used only when the running theme does not define the tokens above.
    private static readonly IBrush _surfaceFallback = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x28));

    private readonly RadialGeometry _geometry;
    private readonly IReadOnlyList<string> _sectorIds;
    private readonly List<Border> _sectorViews = [];
    private readonly Border _centre;

    public RadialMenuView(IReadOnlyList<string> sectorIds, RadialGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(sectorIds);
        ArgumentNullException.ThrowIfNull(geometry);
        _sectorIds = sectorIds;
        _geometry = geometry;
        IsHitTestVisible = false;

        _centre = Label(CentreLabel, geometry.CentreRadiusDp * 2);
        Place(_centre, new GesturePoint(0, 0), geometry.CentreRadiusDp * 2);
        for (var i = 0; i < sectorIds.Count; i++)
        {
            var view = Label(sectorIds[i], 88);
            Place(view, geometry.SectorCentre(i), 88);
            _sectorViews.Add(view);
        }
    }

    /// <summary>The sector currently highlighted, or null when the finger is in the centre or outside.</summary>
    public string? HighlightedSector { get; private set; }

    /// <summary>True while the centre is lit: dictation is live.</summary>
    public bool CentreLit { get; private set; }

    /// <summary>Lights the sector or the centre under the finger. The brushes come from the theme resources by name.</summary>
    public void Highlight(RadialSlot slot, bool dictating)
    {
        HighlightedSector = slot.Kind == RadialSlotKind.Sector ? slot.SectorId : null;
        CentreLit = dictating;
        for (var i = 0; i < _sectorViews.Count; i++)
        {
            Mark(_sectorViews[i], string.Equals(_sectorIds[i], HighlightedSector, StringComparison.Ordinal));
        }

        Mark(_centre, dictating);
    }

    private static void Mark(Border border, bool on) => border.Opacity = on ? 1.0 : 0.55;

    private static IBrush Token(string key, IBrush fallback) =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : fallback;

    private static Border Label(string text, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Opacity = 0.55,
        Background = Token(SurfaceToken, _surfaceFallback),
        Child = new TextBlock
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Token(TextToken, Brushes.White),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        },
    };

    // Offsets are from the menu origin; the origin is the canvas' own (0, 0), so the whole view is moved to the press point.
    private void Place(Border view, GesturePoint offset, double size)
    {
        SetLeft(view, offset.X - (size / 2));
        SetTop(view, offset.Y - (size / 2));
        Children.Add(view);
    }
}
