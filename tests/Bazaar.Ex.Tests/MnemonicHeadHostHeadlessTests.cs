using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bazaar.Client.Input;
using Bazaar.Contracts.Modules;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicHeadHostHeadlessTests
{
    private sealed class MemoryStore : IMnemonicOverrideStore
    {
        public MnemonicOverrideDocument Saved { get; private set; } = MnemonicOverrideDocument.Empty;

        public Task<MnemonicOverrideLoad> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, null));

        public Task SaveAsync(MnemonicOverrideDocument document, CancellationToken ct = default)
        {
            Saved = document;
            return Task.CompletedTask;
        }
    }

    // The overlay layer is attached to a window, so the screen's controls are in the visual tree.
    private sealed record Harness(Window Window, MnemonicHeadHost Host, ContentControl Layer, MemoryStore Store)
    {
        public Control? Open => Layer.Content as Control;
    }

    private static async Task<Harness> StartAsync()
    {
        var layer = new ContentControl();
        var window = new Window { Content = layer };
        window.Show();
        var store = new MemoryStore();
        var host = await MnemonicHeadHost.StartAsync(
            window,
            store,
            new NullMnemonicAnnouncer(),
            c => layer.Content = c,
            c => { if (layer.Content == c) layer.Content = null; },
            ct: TestContext.Current.CancellationToken);
        return new Harness(window, host, layer, store);
    }

    private static void Type(Window window, string text)
    {
        // Letters only: the key for each letter is its upper-case name.
        foreach (var ch in text)
        {
            window.KeyPress(Enum.Parse<Key>(char.ToUpperInvariant(ch).ToString()), RawInputModifiers.None, PhysicalKey.None, ch.ToString());
        }
    }

    [AvaloniaFact]
    public async Task Typed_remap_opens_the_remap_screen_through_the_head()
    {
        var h = await StartAsync();

        Type(h.Window, "remap");
        h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.IsType<MnemonicRemapScreen>(h.Open);
        h.Host.Dispose();
    }

    [AvaloniaFact]
    public async Task Escape_on_the_remap_screen_closes_it()
    {
        var h = await StartAsync();
        h.Host.OpenRemap();
        var screen = Assert.IsType<MnemonicRemapScreen>(h.Open);

        screen.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = screen });

        Assert.Null(h.Open);
        h.Host.Dispose();
    }

    [AvaloniaFact]
    public async Task A_remap_made_on_the_screen_is_saved_and_applies_to_the_next_key()
    {
        var h = await StartAsync();
        h.Host.OpenRemap();
        var screen = (MnemonicRemapScreen)h.Open!;
        Assert.True(screen.Select(NavigationDefaults.SettingsCommandId));
        h.Window.UpdateLayout();
        var input = screen.GetVisualDescendants().OfType<TextBox>().Single();
        input.Text = "zz";
        input.Focus();

        h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.Null(h.Host.LastError);
        Assert.Contains(h.Host.Runtime.Document.Overrides, o => o.CommandId == NavigationDefaults.SettingsCommandId && o.Mnemonic == "zz");
        Assert.Contains(h.Store.Saved.Overrides, o => o.CommandId == NavigationDefaults.SettingsCommandId && o.Mnemonic == "zz");
        h.Host.Dispose();
    }

    private static readonly ClientCommandContribution[] _dispatchCommands =
    [
        new("test.one", "Test one", "tone"),
        new("test.other", "Test other", "tother"),
    ];

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "bazaar-mnemonic-tests", Guid.NewGuid().ToString("N") + ".json");

    private static void TypeThenEnter(Window window, string text)
    {
        foreach (var ch in text)
        {
            window.KeyPress(Enum.Parse<Key>(char.ToUpperInvariant(ch).ToString()), RawInputModifiers.None, PhysicalKey.None, ch.ToString());
        }

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
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
        using var host = await MnemonicHeadHost.AttachAsync(window, TempFile(), _dispatchCommands, dispatcher, _ => { }, TestContext.Current.CancellationToken);

        TypeThenEnter(window, "tone");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["test.one"], ran);
    }

    [AvaloniaFact]
    public async Task A_command_with_no_handler_is_logged_and_nothing_else_happens()
    {
        var window = new Window { Content = new Border() };
        window.Show();
        var log = new List<string>();
        using var host = await MnemonicHeadHost.AttachAsync(window, TempFile(), _dispatchCommands, new ClientCommandDispatcher(), log.Add, TestContext.Current.CancellationToken);

        TypeThenEnter(window, "tother");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(log, line => line.Contains("test.other", StringComparison.Ordinal) && line.Contains("No handler", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Ctrl_chords_pass_through_untouched_when_a_dispatcher_is_attached()
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
        using var host = await MnemonicHeadHost.AttachAsync(window, TempFile(), _dispatchCommands, dispatcher, _ => { }, TestContext.Current.CancellationToken);
        var handled = false;
        window.KeyDown += (_, e) => handled = e.Handled;

        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, "s");

        Assert.False(handled);
        Assert.Empty(ran);
    }

    [Fact]
    public void The_palette_mnemonic_is_the_one_the_editor_claim_table_assumes()
    {
        Assert.Equal("palette", MnemonicHeadSetup.PaletteMnemonic);
    }
}
