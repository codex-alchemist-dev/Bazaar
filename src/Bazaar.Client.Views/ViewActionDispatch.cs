using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

/// <summary>Sends a closed action to the head's <see cref="IViewActions"/>. Adding a case is a schema version change (doc 41c, section 6).</summary>
internal static class ViewActionDispatch
{
    public static Task RunAsync(IViewActions actions, ViewAction action, string? value, IReadOnlyDictionary<string, object>? args = null)
    {
        string target = action.Target ?? throw new ViewDeclarationException("action has no target.");
        if (args is { Count: > 0 })
        {
            return action.Kind switch
            {
                ViewActionKind.Navigate => actions.NavigateAsync(target, args),
                ViewActionKind.Invoke => actions.InvokeAsync(target, args),
                ViewActionKind.OpenPane => actions.OpenPaneAsync(target, args),
                _ => throw new ViewDeclarationException("this action kind takes no arguments."),
            };
        }

        return action.Kind switch
        {
            ViewActionKind.Navigate => actions.NavigateAsync(target),
            ViewActionKind.Invoke => actions.InvokeAsync(target),
            ViewActionKind.OpenPane => actions.OpenPaneAsync(target),
            ViewActionKind.SetSetting => actions.SetSettingAsync(
                target,
                value ?? throw new ViewDeclarationException("setSetting ran without a value.")),
            _ => throw new ViewDeclarationException("unknown action."),
        };
    }
}
