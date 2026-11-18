using Bazaar.Client.Theme;

namespace Bazaar.Client.Views;

/// <summary>The four closed actions a view may run (doc 41c, section 6). The head implements them; a view never sends a raw wire call.</summary>
public interface IViewActions
{
    Task NavigateAsync(string target);

    Task InvokeAsync(string command);

    Task OpenPaneAsync(string paneKind);

    Task SetSettingAsync(string key, string value);
}

/// <summary>
/// Resolves localisation keys (doc 41c, section 7). An unknown key should return the key itself so the gap stays visible.
/// The mapper does not call this yet: the Rubedo view schema has no marker for a localisation key, so every prop string is a literal.
/// </summary>
public interface IViewLocalizer
{
    string Text(string key);
}

/// <summary>
/// Everything the mapping needs from the head. <see cref="OnActionError"/> receives a failure from a click or an input
/// change, because those run after the control is built and cannot return a placeholder.
/// </summary>
public sealed record ViewContext(
    ThemeDefinition Theme,
    IViewActions Actions,
    IViewLocalizer Localizer,
    Action<Exception> OnActionError);

/// <summary>A declaration that cannot be mapped. The mapper turns it into a visible placeholder for that node only.</summary>
public sealed class ViewDeclarationException : Exception
{
    public ViewDeclarationException(string message)
        : base(message)
    {
    }
}
