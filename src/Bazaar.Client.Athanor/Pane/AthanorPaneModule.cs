using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>
/// Registers the Athanor editor pane (<see cref="AthanorPaneKinds.Editor"/>). The head hands the module an
/// <see cref="IEditorHost"/> and an <see cref="IEditorFileSource"/> through <see cref="PaneModuleContext.With{T}"/>; with
/// either missing the pane is its empty state. One host serves one pane, so the module builds one view model.
/// </summary>
public sealed class AthanorPaneModule : IPaneModule, IClientModule
{
    private readonly AthanorClientModule _client = new();

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest => _client.Manifest;

    /// <summary>Records the pane kind and commands the manifest declares.</summary>
    public void Register(ClientModuleBuilder builder) => _client.Register(builder);

    /// <summary>
    /// Runs the module's commands for the pane built by <see cref="Register(PaneRegistry, PaneModuleContext)"/>; null before
    /// that. The head forwards a mnemonic command id to it when <see cref="AthanorCommandHandler.Handles"/> is true.
    /// </summary>
    public AthanorCommandHandler? CommandHandler { get; private set; }

    /// <summary>
    /// Runs one of the module's commands and returns the line to tell the user, or null when it is done and needs none. A
    /// head registers this with its command dispatcher next to <see cref="AthanorCommandHandler.Handles"/>.
    /// </summary>
    public async Task<string?> RunCommandAsync(string commandId, CancellationToken cancellationToken = default)
    {
        if (CommandHandler is null)
        {
            return AthanorPaneViewModel.EmptyMessage;
        }

        var result = await CommandHandler.InvokeAsync(commandId, cancellationToken).ConfigureAwait(true);
        return result.Status == AthanorCommandStatus.Done ? null : result.Reason ?? "Nothing to do.";
    }

    /// <summary>The pane a head shows in its window when it opens the editor.</summary>
    public static Bazaar.Contracts.Panes.PaneDescriptor WindowPane { get; } =
        new(AthanorPaneKinds.Editor, "Athanor", string.Empty, System.Text.Json.JsonElement.Parse("{}"));

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

        CommandHandler = new AthanorCommandHandler(pane, context.Get<IEditorCommandPrompts>());
        registry.Register(AthanorPaneKinds.Editor, _ => pane);
        registry.RegisterView<AthanorPaneViewModel>(AthanorPaneViews.Editor);
    }
}
