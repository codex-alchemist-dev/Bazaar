using Bazaar.Client.Core.Commands;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.TimeMachine;

/// <summary>
/// The Time Machine client module (doc 26 section 4, doc 40): the replay scrubber pane and its palette command.
/// A head adds <see cref="Pane"/> to its pane modules, hands the head's <see cref="IReplayClient"/> over with
/// <c>PaneModuleContext.With</c>, and lists <see cref="OpenCommand"/> among its local commands.
/// </summary>
public sealed class TimeMachineModule : IClientModule
{
    /// <summary>Module id, from doc 40 (the replay scrubber lives in <c>Bazaar.Client.TimeMachine</c>).</summary>
    public const string ModuleId = "bazaar.time-machine";

    /// <summary>Palette command id. The head opens the scrubber pane when it runs.</summary>
    public const string OpenCommandId = "bazaar.replay.open";

    /// <summary>Setting key for the install default view, owned by the server (doc 26, settings table).</summary>
    public const string DefaultViewSetting = "replay.default_view";

    public ModuleManifest Manifest { get; } = new(
        Id: ModuleId,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: [ReplayScrubberViewModel.PaneKind],
        Commands: [OpenCommandId],
        SettingKeys: [DefaultViewSetting],
        Capabilities: ["wire:gc.history", "ui:pane"],
        Isolation: ModuleIsolation.InProcessCompiled,
        Host: new HostRequirements(HostRequirements.CurrentSchemaVersion, ["windows", "linux", "android"], ["x64", "arm64"], []));

    /// <summary>The pane module for <c>PaneRegistry.Compose</c>.</summary>
    public static IPaneModule Pane { get; } = new ReplayScrubberPaneModule();

    /// <summary>
    /// The palette command. Keyboard-first: the mnemonic is plain letters, chorded on Enter, with no Ctrl shortcut.
    /// </summary>
    public static Command OpenCommand { get; } = new(
        OpenCommandId,
        "Open replay scrubber",
        "replay",
        ["history", "time machine", "rewind", "scrub", "fork"],
        CommandSource.Local);

    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddPaneKind(ReplayScrubberViewModel.PaneKind);
        builder.AddCommand(OpenCommandId);
    }
}

/// <summary>Registers the replay scrubber pane (<see cref="ReplayScrubberViewModel.PaneKind"/>).</summary>
public sealed class ReplayScrubberPaneModule : IPaneModule
{
    public string Id => ReplayScrubberViewModel.PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var client = context.Get<IReplayClient>();
        registry.Register(ReplayScrubberViewModel.PaneKind, descriptor => ReplayScrubberViewModel.FromModel(descriptor.Model, client));
        registry.RegisterView<ReplayScrubberViewModel>(vm => ReplayScrubberView.Build(vm));
    }
}
