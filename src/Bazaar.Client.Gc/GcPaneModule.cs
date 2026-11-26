using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Gc;

/// <summary>Registers the group chat pane (<see cref="GcPaneViewModel.PaneKind"/>).</summary>
public sealed class GcPaneModule : IPaneModule, IClientModule
{
    /// <summary>An empty GC model. The GC pane takes its content from the chat, not the descriptor.</summary>
    private static readonly JsonElement _emptyModel = JsonElement.Parse("{}");

    /// <summary>Command id that opens the first GC in the list.</summary>
    public const string FirstCommandId = "nav.gc.first";

    /// <summary>Command id that opens the GC with the most recent activity.</summary>
    public const string TopCommandId = "nav.gc.top";

    /// <summary>The commands this module contributes, with their default mnemonics.</summary>
    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(FirstCommandId, "GC list, first chat", "ngc1"),
        new(TopCommandId, "Most recent activity GC", "topgc"),
    ];

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(GcPaneModule), GcPaneViewModel.PaneKind, [GcPaneViewModel.PaneKind], ["ui:pane", "wire:gc.history", "wire:gc.post"], Commands.Select(c => c.Id).ToArray());

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }

        foreach (var command in Commands)
        {
            builder.AddCommand(command);
        }
    }

    public string Id => GcPaneViewModel.PaneKind;

    /// <remarks>
    /// The chat and its view come from the head through the context (<see cref="IChatViewModel"/> and
    /// <see cref="IChatViewFactory"/>). With neither, the pane shows its empty state.
    /// </remarks>
    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(GcPaneViewModel.PaneKind, descriptor => new GcPaneViewModel(descriptor, context.Get<IChatViewModel>()));
        registry.RegisterView<GcPaneViewModel>(gc => GcPaneView.Create(gc, context.Get<IChatViewFactory>()));
    }

    /// <summary>
    /// The phone body of the GC pane, or null for any other kind. A head passes it to the phone pager (see
    /// <c>FirstPartyPanes.PhonePaneBodies</c>) so the GC pane renders on the phone too.
    /// </summary>
    public static Control? PhoneBody(string kind, PaneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // The GC pane has no model of its own: the registry's chat supplies it, or the pane shows its empty state.
        return kind == GcPaneViewModel.PaneKind
            ? registry.CreateView(registry.Create(new PaneDescriptor(kind, GcPaneViewModel.DefaultTitle, string.Empty, _emptyModel)))
            : null;
    }
}
