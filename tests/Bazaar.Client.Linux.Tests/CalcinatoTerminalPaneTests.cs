using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Avalonia;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Linux;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Linux.Tests;

/// <summary>
/// Proves the Calcinato terminal pane is composed into the head's registry and draws as a <c>TerminalControl</c>.
/// The host is a fake, so no PTY is opened.
/// </summary>
public class CalcinatoTerminalPaneTests
{
    [AvaloniaFact]
    public void Terminal_pane_kind_resolves_to_a_terminal_control()
    {
        var sessions = new TerminalSessionManager(new FakeTerminalHost());
        var registry = CalcinatoTerminalPanes.Compose(PaneViews.Instance, CalcinatoPanes.Modules, new PaneModuleContext(), sessions);

        Assert.Contains(PaneKinds.Terminal, registry.Kinds);
        var pane = registry.Create(new PaneDescriptor(PaneKinds.Terminal, "Terminal", string.Empty, JsonElement.Parse("{}")));
        var view = Assert.IsType<StackPanel>(registry.CreateView(pane));

        // The registry puts the pane title above the body; the body is the terminal control.
        Assert.IsType<TerminalControl>(view.Children[1]);
    }

    [AvaloniaFact]
    public async Task Show_terminal_opens_one_local_shell_and_draws_the_terminal_control()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The Linux head's PTY runs on Linux only.");
        }

        await using var sessions = CalcinatoTerminalPanes.CreateSessions();
        var window = new MainWindow();

        CalcinatoTerminalPanes.ShowTerminal(window, sessions);

        Assert.Single(sessions.Sessions);
        Assert.NotNull(sessions.Active);
        var scroll = Assert.IsType<ScrollViewer>(window.Content);
        var stack = Assert.IsType<StackPanel>(scroll.Content);
        var pane = Assert.IsType<StackPanel>(Assert.Single(stack.Children));
        Assert.IsType<TerminalControl>(pane.Children[1]);
    }

    /// <summary>A host with no local backend and no remote streams. Opening anything fails, which the test never does.</summary>
    private sealed class FakeTerminalHost : ITerminalHost
    {
        public bool SupportsLocal => false;

        public ITerminalSource OpenLocal(TerminalSpec spec) => throw new NotSupportedException("Fake host: no local terminals.");

        public Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct) =>
            throw new NotSupportedException("Fake host: no remote terminals.");
    }
}
