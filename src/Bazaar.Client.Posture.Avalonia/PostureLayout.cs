using Avalonia;
using Avalonia.Controls;

namespace Bazaar.Client.Posture.Avalonia;

/// <summary>
/// Attached properties that carry the posture down the visual tree (doc 93: content never straddles the hinge). Both inherit, so
/// any control can read the hinge gap and the layout class of the window it sits in without a reference to the driver.
/// </summary>
public static class PostureLayout
{
    /// <summary>The hinge as an inset (orientation, offset from the leading edge, size). <see cref="HingeInset.None"/> when there is no hinge.</summary>
    public static readonly AttachedProperty<HingeInset> HingeInsetProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, HingeInset>("HingeInset", HingeInset.None, inherits: true);

    /// <summary>The layout class in force for the window.</summary>
    public static readonly AttachedProperty<PostureLayoutClass> LayoutClassProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, PostureLayoutClass>("LayoutClass", PostureLayoutClass.Folded, inherits: true);

    public static HingeInset GetHingeInset(Control control) => control.GetValue(HingeInsetProperty);

    public static void SetHingeInset(Control control, HingeInset value) => control.SetValue(HingeInsetProperty, value);

    public static PostureLayoutClass GetLayoutClass(Control control) => control.GetValue(LayoutClassProperty);

    public static void SetLayoutClass(Control control, PostureLayoutClass value) => control.SetValue(LayoutClassProperty, value);
}
