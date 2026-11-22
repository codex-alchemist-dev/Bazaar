using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Contracts.Modules;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicHeadSetupHeadlessTests
{
    private static void Press(Window window, Key key, string? symbol = null) =>
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, symbol);

    [AvaloniaFact]
    public async Task A_module_command_is_reachable_by_its_mnemonic_and_the_window_content_is_kept()
    {
        var content = new TextBox();
        var window = new Window { Content = content };
        window.Show();
        var path = Path.Combine(Path.GetTempPath(), $"bazaar-mnemonics-{Guid.NewGuid():N}.json");
        var logged = new List<string>();

        using var shell = await MnemonicHeadSetup.InstallAsync(
            window,
            window,
            [new ClientCommandContribution("demo.open", "Open the demo", "opendemo")],
            path,
            logged.Add,
            TestContext.Current.CancellationToken);
        var commands = new List<string>();
        shell.CommandInvoked += (_, id) => commands.Add(id);

        foreach (var c in "opendemo")
        {
            Press(window, Enum.Parse<Key>(char.ToUpperInvariant(c).ToString()), c.ToString());
        }

        Press(window, Key.Enter);

        Assert.True(shell.IsActive);
        Assert.Equal(["demo.open"], commands);
        var grid = Assert.IsType<Grid>(window.Content);
        Assert.Contains(content, grid.Children);
        Assert.Empty(logged);
    }

    [AvaloniaFact]
    public async Task A_module_that_contributes_a_shell_command_replaces_the_shell_default_instead_of_clashing()
    {
        var window = new Window { Content = new TextBox() };
        window.Show();
        var path = Path.Combine(Path.GetTempPath(), $"bazaar-mnemonics-{Guid.NewGuid():N}.json");
        var logged = new List<string>();

        using var shell = await MnemonicHeadSetup.InstallAsync(
            window,
            window,
            [new ClientCommandContribution(NavigationDefaults.ConstellationCommandId, "Constellation", "constellation")],
            path,
            logged.Add,
            TestContext.Current.CancellationToken);

        Assert.True(shell.IsActive);
        Assert.Empty(logged);
    }

    [AvaloniaFact]
    public async Task The_head_host_takes_module_commands_and_lets_a_module_own_a_shell_command()
    {
        var window = new Window { Content = new TextBox() };
        window.Show();
        var path = Path.Combine(Path.GetTempPath(), $"bazaar-mnemonics-{Guid.NewGuid():N}.json");

        using var host = await MnemonicHeadHost.AttachAsync(
            window,
            path,
            [
                new ClientCommandContribution("demo.open", "Open the demo", "opendemo"),
                new ClientCommandContribution(NavigationDefaults.ConstellationCommandId, "Constellation", "constellation"),
            ],
            TestContext.Current.CancellationToken);
        var commands = new List<string>();
        host.Shell.CommandInvoked += (_, id) => commands.Add(id);

        foreach (var c in "opendemo")
        {
            Press(window, Enum.Parse<Key>(char.ToUpperInvariant(c).ToString()), c.ToString());
        }

        Press(window, Key.Enter);

        Assert.Equal(["demo.open"], commands);
        Assert.Null(host.Runtime.Warning);
    }
}
