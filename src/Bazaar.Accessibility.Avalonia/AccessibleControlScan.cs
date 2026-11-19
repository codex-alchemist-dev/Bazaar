using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Bazaar.Accessibility;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>One control that failed the accessible-name check.</summary>
/// <param name="ControlType">The control's type name, for example <c>Button</c>.</param>
/// <param name="Name">The control's Name, or null, so a developer can find it.</param>
/// <param name="Problem">The reason from <see cref="AccessibleName.Problem"/>.</param>
public sealed record AccessibleControlProblem(string ControlType, string? Name, string Problem);

/// <summary>
/// Checks every interactive control under a root (doc 49 section 2): the explicit <see cref="AutomationProperties.NameProperty"/>
/// (a CI check fails when a control has no accessible name), and that a shown, enabled control is a keyboard tab stop. It reads the explicit property on purpose:
/// the automation peer falls back to visible text, so an unnamed button would otherwise pass (spike, PR #560).
/// </summary>
public static class AccessibleControlScan
{
    /// <summary>Control types that a screen reader user can act on. Other controls are not checked.</summary>
    public static readonly IReadOnlyList<Type> InteractiveTypes =
    [
        typeof(Button),
        typeof(ToggleButton),
        typeof(CheckBox),
        typeof(RadioButton),
        typeof(TextBox),
        typeof(ComboBox),
        typeof(Slider),
        typeof(ListBox),
    ];

    /// <summary>The problem text for a control that a keyboard user cannot reach.</summary>
    public const string KeyboardProblem = "The control cannot receive keyboard focus.";

    // Doc 49 section 2: every feature is reachable from the keyboard. A control that is shown and enabled must be focusable
    // and a tab stop. Hidden and disabled controls are skipped, because they are not offered to anyone.
    // A ListBox is a composite: it is not a tab stop itself, its items take the focus, so an empty list has nothing to
    // reach and a populated one is reachable when an item can take focus.
    private static bool IsKeyboardReachable(Control control)
    {
        if (!control.IsEffectivelyVisible || !control.IsEffectivelyEnabled)
        {
            return true;
        }

        if (control is ListBox list)
        {
            return list.GetLogicalDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyEnabled).All(i => i.Focusable);
        }

        return control.Focusable && KeyboardNavigation.GetIsTabStop(control);
    }

    /// <summary>Returns every interactive control under <paramref name="root"/> (inclusive) that fails the name check.</summary>
    public static IReadOnlyList<AccessibleControlProblem> Find(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var problems = new List<AccessibleControlProblem>();
        IEnumerable<Control> candidates = root.GetSelfAndLogicalDescendants().OfType<Control>();
        foreach (Control control in candidates)
        {
            if (!InteractiveTypes.Any(t => t.IsInstanceOfType(control)))
            {
                continue;
            }

            string? problem = AccessibleName.Problem(AutomationProperties.GetName(control));
            if (problem is not null)
            {
                problems.Add(new AccessibleControlProblem(control.GetType().Name, control.Name, problem));
            }

            if (!IsKeyboardReachable(control))
            {
                problems.Add(new AccessibleControlProblem(control.GetType().Name, control.Name, KeyboardProblem));
            }
        }

        return problems;
    }
}
