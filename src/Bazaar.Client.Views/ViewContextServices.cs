using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Views;

/// <summary>Carries the head's <see cref="ViewContext"/> to modules through the pane module context (doc 41c, section 11.1).</summary>
public static class ViewContextServices
{
    /// <summary>Returns a copy of <paramref name="context"/> that carries <paramref name="view"/>.</summary>
    public static PaneModuleContext WithViewContext(this PaneModuleContext context, ViewContext view)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(view);

        return context.With(view);
    }

    /// <summary>The head's view context, or null when the head supplied none (the module then keeps its code-built pane).</summary>
    public static ViewContext? GetViewContext(this PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Get<ViewContext>();
    }

    /// <summary>
    /// A head's default view context: the built-in dark theme, the given actions, a key-returning localiser unless one is given,
    /// and an error sink. Heads call this once at start-up.
    /// </summary>
    public static ViewContext CreateDefault(IViewActions actions, Action<Exception> onActionError, IViewLocalizer? localizer = null) =>
        new(BuiltInThemes.Dark, actions, localizer ?? new KeyLocalizer(), onActionError);

    private sealed class KeyLocalizer : IViewLocalizer
    {
        public string Text(string key) => key;
    }
}
