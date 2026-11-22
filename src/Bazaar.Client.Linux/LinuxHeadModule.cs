using Bazaar.Client.Calcinato.Mnemonics;
using Bazaar.Client.Constellation;
using Bazaar.Client.Gc;
using Bazaar.Client.Linux.Updates;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Linux;

/// <summary>
/// The client module of the Linux head itself: the palette commands no feature module owns. These are "open this pane" commands
/// for panes that have no navigation command of their own, the update check, the default-app and default-terminal
/// registration commands (doc 30 section 1), and Quit. A feature module's own navigation commands (<c>nav.gc.first</c> and so
/// on) stay with that module; <see cref="ModuleNavigation"/> only says which pane each one opens.
/// </summary>
public sealed class LinuxHeadModule : IClientModule
{
    /// <summary>The module id in the client manifest.</summary>
    public const string ModuleId = "bazaar.linux.head";

    /// <summary>Id of the Quit command.</summary>
    public const string QuitCommandId = "app.quit";

    /// <summary>A command that opens one pane kind.</summary>
    /// <param name="Command">The palette entry and its default mnemonic.</param>
    /// <param name="PaneKind">The pane kind it opens.</param>
    public sealed record PaneCommand(ClientCommandContribution Command, string PaneKind);

    /// <summary>The head's own pane commands.</summary>
    public static IReadOnlyList<PaneCommand> PaneCommands { get; } =
    [
        new(new("nav.dashboard", "Dashboard", "dashboard"), PaneKinds.Dashboard),
        new(new("nav.vault", "Vault search", "vault"), PaneKinds.VaultSearch),
        new(new("nav.org", "Org chart", "org"), PaneKinds.Org),
        new(new("nav.calendar", "Company calendar", "calendar"), PaneKinds.Calendar),
        new(new("nav.interrupts", "Interrupt stack", "interrupts"), PaneKinds.InterruptStack),
        new(new("nav.ryusei.schedule", "RyuseiFuda schedule", "schedule"), PaneKinds.RyuseiSchedule),
        new(new("nav.ryusei.workday", "RyuseiFuda Workday", "workday"), PaneKinds.Workday),
        new(new(CalcinatoCommands.TerminalCommandId, "Terminal, open the terminal pane", "terminal"), PaneKinds.Terminal),
    ];

    /// <summary>The pane each command owned by another module (or by the shell defaults) opens.</summary>
    public static IReadOnlyDictionary<string, string> ModuleNavigation { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GcPaneModule.FirstCommandId] = GcPaneViewModel.PaneKind,
        [GcPaneModule.TopCommandId] = GcPaneViewModel.PaneKind,
        [ConstellationPaneModule.OpenCommandId] = PaneKinds.Constellation,
        [KanbanPaneModule.OpenCommandId] = PaneKinds.Kanban,
        [RoadmapPaneModule.OpenCommandId] = PaneKinds.Roadmap,
        // The unread list lives on the Dashboard (doc 28, start screen).
        ["nav.unread"] = PaneKinds.Dashboard,
    };

    /// <summary>The non-navigation commands: update check, default app, default terminal, Quit.</summary>
    public static IReadOnlyList<ClientCommandContribution> ActionCommands { get; } =
    [
        new(UpdateCoordinator.CheckCommandId, UpdateCheckCommand.Command.Title, "update"),
        new(DefaultAppCommands.RegisterId, DefaultAppCommands.All[0].Title, "regapp"),
        new(DefaultAppCommands.StatusId, DefaultAppCommands.All[1].Title, "appstatus"),
        new(DefaultAppCommands.UnregisterId, DefaultAppCommands.All[2].Title, "unregapp"),
        new(DefaultTerminalCommands.RegisterId, DefaultTerminalCommands.All[0].Title, "regterm"),
        new(DefaultTerminalCommands.StatusId, DefaultTerminalCommands.All[1].Title, "termstatus"),
        new(DefaultTerminalCommands.UnregisterId, DefaultTerminalCommands.All[2].Title, "unregterm"),
        new(QuitCommandId, "Quit", "quit"),
    ];

    private static IEnumerable<ClientCommandContribution> AllCommands() => PaneCommands.Select(p => p.Command).Concat(ActionCommands);

    /// <inheritdoc />
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(
        typeof(LinuxHeadModule),
        ModuleId,
        [],
        ["storage:local", "process:spawn"],
        AllCommands().Select(c => c.Id).ToArray());

    /// <inheritdoc />
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var command in AllCommands())
        {
            builder.AddCommand(command);
        }
    }
}
