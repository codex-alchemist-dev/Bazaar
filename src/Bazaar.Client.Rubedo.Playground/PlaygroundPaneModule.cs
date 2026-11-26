using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Rubedo.Playground;

/// <summary>
/// Registers the Rubedo playground pane (doc 41d section 7). It is an optional module: <see cref="EnabledByDefault"/> is
/// false, so no head lists it unless the company's module list names it (doc 41d section 9, question 1).
/// </summary>
public sealed class PlaygroundPaneModule : IPaneModule, IClientModule
{
    /// <summary>Command id that opens the playground.</summary>
    public const string OpenCommandId = "nav.rubedo.playground";

    /// <summary>Optional modules are left out of a head's default list (doc 41d section 9, question 1: proposed).</summary>
    public const bool EnabledByDefault = false;

    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(OpenCommandId, "Rubedo playground", "rubedoplay", ["rbdplay"]),
    ];

    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(
        typeof(PlaygroundPaneModule), PlaygroundViewModel.PaneKind, [PlaygroundViewModel.PaneKind], ["ui:pane"], Commands.Select(c => c.Id).ToArray());
        typeof(PlaygroundPaneModule), PlaygroundViewModel.PaneKind, [PlaygroundViewModel.PaneKind], ["ui:pane"], Commands.Select(c => c.Id).ToArray(), enabledByDefault: EnabledByDefault);

    public string Id => PlaygroundViewModel.PaneKind;

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

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PlaygroundViewModel.PaneKind, _ => new PlaygroundViewModel());
        registry.RegisterView<PlaygroundViewModel>(PlaygroundView.Build);
    }
}
