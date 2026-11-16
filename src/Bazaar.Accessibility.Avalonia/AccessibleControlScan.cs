using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Bazaar.Accessibility;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>One control that failed the accessible-name check.</summary>
/// <param name="ControlType">The control's type name, for example <c>Button</c>.</param>
/// <param name="Name">The control's Name, or null, so a developer can find it.</param>
/// <param name="Problem">The reason from <see cref="AccessibleName.Problem"/>.</param>
public sealed record AccessibleControlProblem(string ControlType, string? Name, string Problem);

/// <summary>
/// Checks the explicit <see cref="AutomationProperties.NameProperty"/> of every interactive control under a root (doc
/// 49 section 2: a CI check fails when a control has no accessible name). It reads the explicit property on purpose:
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
    ];

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
        }

        return problems;
    }
}
