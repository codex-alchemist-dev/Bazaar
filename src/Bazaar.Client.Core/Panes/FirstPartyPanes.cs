using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Registers the panes the client shell ships with. Client.Core ships no pane modules of its own: the Dashboard, GC,
/// Constellation and the rest are feature modules that a head composes and passes to <see cref="CreateRegistry"/>.
/// </summary>
public static class FirstPartyPanes
{
    /// <param name="modules">The pane modules the head ships, in pager order (doc 29).</param>
    /// <param name="report">Called once per kind that falls back to the generic view.</param>
    /// <param name="features">
    /// The feature dependencies the head injects with <see cref="PaneModuleContext.With{T}"/>, for example the Constellation
    /// tile source or the group chat the GC pane shows. Null adds none.
    /// </param>
    public static PaneRegistry CreateRegistry(
        IEnumerable<IPaneModule> modules,
        Action<string>? report = null,
        PaneModuleContext? features = null) =>
        PaneRegistry.Compose(PaneViews.Instance, modules, CreateContext(report, features));

    /// <summary>Hands the report callback to the context; an absent one adds nothing.</summary>
    private static PaneModuleContext CreateContext(Action<string>? report, PaneModuleContext? features)
    {
        var context = features ?? new PaneModuleContext();
        if (report is not null) context = context with { Report = report };
        return context;
    }

    /// <summary>
    /// Pane bodies for the phone pager: the first of <paramref name="bodies"/> that returns a control for a kind wins. A head
    /// passes the feature modules' phone bodies, for example <c>RyuseiFudaPanes.PhoneBody</c> and <c>GcPaneModule.PhoneBody</c>.
    /// A kind no body claims returns null, which keeps the pager's kind-name placeholder.
    /// </summary>
    public static Func<string, Control?> PhonePaneBodies(params Func<string, Control?>[] bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);

        return kind => bodies.Select(body => body(kind)).FirstOrDefault(control => control is not null);
    }
}
