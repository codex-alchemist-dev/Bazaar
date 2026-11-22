using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Client.Input;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicShellHeadlessTests
{
    private sealed class MemoryStore : IMnemonicOverrideStore
    {
        public Task<MnemonicOverrideLoad> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, null));

        public Task SaveAsync(MnemonicOverrideDocument document, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Harness(Window Window, MnemonicRuntime Runtime, MnemonicShell Shell, List<string> Commands, List<MnemonicPaletteView> Shown);

    private static async Task<Harness> BuildAsync()
    {
        var runtime = await MnemonicRuntime.CreateAsync(
            NavigationDefaults.Assignments,
            new MemoryStore(),
            [KeyStroke.OfChar('p')],
            new MnemonicNoticeLog(_ => { }),
            ct: TestContext.Current.CancellationToken);
        var region = new MnemonicLiveRegion();
        var window = new Window { Content = new StackPanel { Children = { new TextBox(), region.Control } } };
        window.Show();
        var shown = new List<MnemonicPaletteView>();
        var shell = new MnemonicShell(window, runtime, region, shown.Add, _ => { });
        var commands = new List<string>();
        shell.CommandInvoked += (_, id) => commands.Add(id);
        return new Harness(window, runtime, shell, commands, shown);
    }

    private static void Press(Window window, Key key, string? symbol = null) =>
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, symbol);

    [AvaloniaFact]
    public async Task A_typed_mnemonic_and_Enter_reports_the_command()
    {
        var h = await BuildAsync();

        Press(h.Window, Key.N, "n");
        Press(h.Window, Key.G, "g");
        Press(h.Window, Key.C, "c");
        Press(h.Window, Key.D1, "1");
        Press(h.Window, Key.Enter);

        Assert.True(h.Shell.IsActive);
        Assert.Equal([NavigationDefaults.GcFirstCommandId], h.Commands);
        h.Shell.Dispose();
    }

    [AvaloniaFact]
    public async Task The_palette_key_opens_the_palette_and_Enter_chooses_the_selected_command()
    {
        var h = await BuildAsync();

        Press(h.Window, Key.P, "p");
        Press(h.Window, Key.Enter);
        Assert.True(h.Shell.IsPaletteOpen);
        var view = Assert.Single(h.Shown);
        var expected = view.Selected!.CommandId;

        Press(h.Window, Key.Enter);

        Assert.False(h.Shell.IsPaletteOpen);
        Assert.Equal([expected], h.Commands);
        h.Shell.Dispose();
    }

    [AvaloniaFact]
    public async Task A_remap_takes_effect_on_the_next_key()
    {
        var h = await BuildAsync();
        var result = await h.Runtime.RemapAsync(NavigationDefaults.SettingsCommandId, "zz", TestContext.Current.CancellationToken);
        Assert.True(result.Accepted);

        Press(h.Window, Key.Z, "z");
        Press(h.Window, Key.Z, "z");
        Press(h.Window, Key.Enter);

        Assert.Equal([NavigationDefaults.SettingsCommandId], h.Commands);
        h.Shell.Dispose();
    }

    [AvaloniaFact]
    public async Task Disposing_stops_the_shell_reacting_to_keys()
    {
        var h = await BuildAsync();
        h.Shell.Dispose();

        Press(h.Window, Key.N, "n");
        Press(h.Window, Key.G, "g");
        Press(h.Window, Key.C, "c");
        Press(h.Window, Key.D1, "1");
        Press(h.Window, Key.Enter);

        Assert.Empty(h.Commands);
    }
}
