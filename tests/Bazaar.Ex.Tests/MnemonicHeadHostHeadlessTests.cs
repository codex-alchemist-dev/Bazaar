using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Contracts.Modules;
using Bazaar.Ex;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicHeadHostHeadlessTests
{
    private static readonly ClientCommandContribution[] _commands =
    [
        new("test.one", "Test one", "tone"),
        new("test.other", "Test other", "tother"),
    ];

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "bazaar-mnemonic-tests", Guid.NewGuid().ToString("N") + ".json");

    private static void Type(Window window, string text)
    {
        foreach (var c in text)
        {
            var key = c is >= 'a' and <= 'z' ? Enum.Parse<Key>(char.ToUpperInvariant(c).ToString()) : Key.D0;
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, c.ToString());
        }
    }

    [AvaloniaFact]
    public async Task A_typed_mnemonic_plus_Enter_reaches_the_registered_handler()
    {
        var window = new Window { Content = new Border() };
        window.Show();
        var ran = new List<string>();
        var dispatcher = new ClientCommandDispatcher();
        dispatcher.Add(id => id == "test.one", (id, _) =>
        {
            ran.Add(id);
            return Task.FromResult<string?>(null);
        });
        using var host = MnemonicHeadHost.Start(window, _commands, TempFile(), dispatcher, _ => { });
        Assert.NotNull(host);

        Type(window, "tone");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["test.one"], ran);
    }

    [AvaloniaFact]
    public async Task A_command_with_no_handler_is_logged_and_nothing_else_happens()
    {
        var window = new Window { Content = new Border() };
        window.Show();
        var log = new List<string>();
        using var host = MnemonicHeadHost.Start(window, _commands, TempFile(), new ClientCommandDispatcher(), log.Add);

        Type(window, "tother");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(log, line => line.Contains("test.other", StringComparison.Ordinal) && line.Contains("No handler", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Ctrl_chords_pass_through_untouched()
    {
        var window = new Window { Content = new Border() };
        window.Show();
        var ran = new List<string>();
        var dispatcher = new ClientCommandDispatcher();
        dispatcher.Add(_ => true, (id, _) =>
        {
            ran.Add(id);
            return Task.FromResult<string?>(null);
        });
        using var host = MnemonicHeadHost.Start(window, _commands, TempFile(), dispatcher, _ => { });
        var handled = false;
        window.KeyDown += (_, e) => handled = e.Handled;

        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, "s");

        Assert.False(handled);
        Assert.Empty(ran);
    }

    [AvaloniaFact]
    public void The_palette_mnemonic_is_the_one_the_editor_claim_table_assumes()
    {
        Assert.Equal("palette", MnemonicHeadHost.PaletteMnemonic);
    }
}
