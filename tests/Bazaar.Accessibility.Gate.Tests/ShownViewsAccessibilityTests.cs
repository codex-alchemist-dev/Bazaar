using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Engines;
using Bazaar.Client.RyuseiFuda.Workday;
using Bazaar.Client.Souls;
using Bazaar.Client.Vault;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Soul;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Testing;

namespace Bazaar.Accessibility.Gate.Tests;

/// <summary>
/// CI gate for doc 49 section 2: every view a person is shown must have an accessible name on each interactive
/// control. Each test builds one shown view headlessly with in-memory fakes (no network, no device) and asserts that
/// <see cref="AccessibleControlScan.Find"/> returns nothing. Failures are not silenced: a failing view fails the build.
/// </summary>
/// <remarks>
/// Covered: the shared Client.Core views that the Windows and Linux heads show (start window, group chat window,
/// phone shell, Soul profile card, vault search pane, engine pane).
/// Skipped: the Android head (<c>Bazaar.Client.Android</c>). Its <c>App.axaml</c> and <c>MainActivity</c> need the
/// Android runtime and cannot be constructed on a desktop CI host. The Windows and Linux heads define no windows of
/// their own (<c>Program.cs</c> only calls into Client.Core), so nothing else is skipped.
/// </remarks>
public class ShownViewsAccessibilityTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _soul = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly Guid _vault = Guid.Parse("0196a6f0-0000-7000-8000-000000000003");

    [AvaloniaFact]
    public void Start_window_has_named_controls()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Group_chat_window_has_named_controls()
    {
        // The session is never started, so no connection is opened. The transport below fails if anything tries.
        var client = new WireClient(new NoConnectionTransport(), new JsonWireCodec(), new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "accessibility-gate", DeviceClass = "linux", ClientBuild = "gate" }, new FakeClock());
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember>(), "me", TimeZoneInfo.Utc);
        var session = new GcChatSession(client, new ChatConnectionSettings(new Uri("ws://gate.invalid/ws"), "t", _gc, "me"), timeline);
        using var chat = new ChatViewModel(session);

        var window = new MainWindow(ChatViewFactory.Instance.Create(chat));
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Phone_shell_has_named_controls()
    {
        var window = new Window { Content = new PhoneShell(), Width = 360, Height = 740 };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Soul_profile_view_has_named_controls()
    {
        var profile = new SoulProfile(_soul, "Sample Soul", "Reviewer", "haiku");
        var version = new SoulVersion(_soul, 1, null, new string('a', 64), "{}", new string('b', 64), "gate", "initial",
            null, DateTimeOffset.UnixEpoch);
        var window = new Window { Content = new SoulProfileView { DataContext = new SoulProfileViewModel(profile, [version]) } };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Vault_search_view_has_named_controls()
    {
        using var http = new HttpClient();
        var descriptor = new Bazaar.Contracts.Panes.PaneDescriptor("vault.search", "Vault", "vault.read", JsonElement.Parse($$"""{"vault":"{{_vault}}"}"""));
        var window = new Window { Content = new VaultSearchView(new VaultSearchPaneViewModel(descriptor, new VaultApi(http, () => null))) };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Kanban_board_view_has_named_controls()
    {
        // KanbanBoardView builds from a plain view model: no services, sockets or files are involved.
        var card = new KanbanCard(Guid.Parse("0196a6f0-0000-7000-8000-000000000004"), "Sample task", KanbanStates.Ready,
            null, [], []);
        var board = new KanbanBoardViewModel([card]);
        board.Refuse(card.Id, "Sample refusal");
        var window = new Window { Content = new KanbanBoardView { DataContext = board } };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Engine_pane_view_has_named_controls()
    {
        var descriptor = new PaneDescriptor("engine.scene", "Scene", EngineCapability.Scenes, JsonElement.Parse("{}"));
        var window = new Window { Content = new EnginePaneView(new EnginePaneViewModel(descriptor)) };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }
}

/// <summary>A transport that fails the test if a connection is attempted. Views here must not open sockets.</summary>
internal sealed class NoConnectionTransport : IWireTransportFactory
{
    public ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The accessibility gate must not open a connection.");
}

