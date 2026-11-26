using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Binds a control's look to theme tokens by name (docs/design/93, tokens; the keys are the ones the theme generator writes), so a
/// view holds no colour or size of its own and a theme switch restyles it live.
/// </summary>
public static class ThemeBinding
{
    /// <summary>Binds a brush property to a colour token such as <c>color.accent.default</c>.</summary>
    public static void BindBrush(Control control, AvaloniaProperty<IBrush?> property, string colourToken) =>
        control.Bind(property, control.GetResourceObservable("Bazaar.brush." + colourToken));

    /// <summary>Calls <paramref name="apply"/> with the token's length now and whenever the theme changes it. Does nothing while the theme has none.</summary>
    public static IDisposable BindLength(Control control, string lengthToken, Action<double> apply) =>
        control.GetResourceObservable("Bazaar." + lengthToken).Subscribe(new LengthObserver(apply));

    /// <summary>Binds a border's padding to two spacing tokens such as <c>space.sm</c>, sideways then up and down.</summary>
    public static void BindPadding(Border border, string horizontalToken, string verticalToken)
    {
        double h = 0, v = 0;
        void Apply() => border.Padding = new Thickness(h, v);
        BindLength(border, horizontalToken, x => { h = x; Apply(); });
        BindLength(border, verticalToken, x => { v = x; Apply(); });
    }

    /// <summary>Binds a border's corner radius to a radius token such as <c>radius.sm</c>.</summary>
    public static void BindRadius(Border border, string radiusToken) =>
        BindLength(border, radiusToken, r => border.CornerRadius = new CornerRadius(r));

    private sealed class LengthObserver(Action<double> apply) : IObserver<object?>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(object? value)
        {
            if (value is double d) apply(d);
        }
    }
}
