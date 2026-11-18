using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>
/// Registers the Athanor editor pane (<see cref="AthanorPaneKinds.Editor"/>). The head hands the module an
/// <see cref="IEditorHost"/> and an <see cref="IEditorFileSource"/> through <see cref="PaneModuleContext.With{T}"/>; with
/// either missing the pane is its empty state. One host serves one pane, so the module builds one view model.
/// </summary>
public sealed class AthanorPaneModule : IPaneModule
{
    public string Id => AthanorPaneKinds.Editor;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var host = context.Get<IEditorHost>();
        var source = context.Get<IEditorFileSource>();
        var drafts = context.Get<IEditorDraftStore>() ?? new InMemoryEditorDraftStore();
        var pane = host is not null && source is not null
            ? new AthanorPaneViewModel("Athanor", host, source, drafts)
            : new AthanorPaneViewModel("Athanor");

        registry.Register(AthanorPaneKinds.Editor, _ => pane);
        registry.RegisterView<AthanorPaneViewModel>(AthanorPaneViews.Editor);
    }
}
