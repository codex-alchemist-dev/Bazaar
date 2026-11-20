using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Avalonia;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Windows;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Windows.Tests;

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

    /// <summary>A host with no local backend and no remote streams. Opening anything fails, which the test never does.</summary>
    private sealed class FakeTerminalHost : ITerminalHost
    {
        public bool SupportsLocal => false;

        public ITerminalSource OpenLocal(TerminalSpec spec) => throw new NotSupportedException("Fake host: no local terminals.");

        public Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct) =>
            throw new NotSupportedException("Fake host: no remote terminals.");
    }
}
