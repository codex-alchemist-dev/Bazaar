#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;
using static Bazaar.Client.Calcinato.Tests.Modal.Input;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ModalCommandLineTests
{
    private static FakeScreen Screen(params string[] visible) =>
        new(rows: Math.Max(1, visible.Length), cols: 40, scrollback: Array.Empty<string>(), visible: visible);

    private static ModalEngine Engine(FakeScreen screen, IMnemonicBridge? mnemonics = null) => new(screen, mnemonics);

    [Fact]
    public void ColonOpensTheCommandLineAndEchoesThePrompt()
    {
        var engine = Engine(Screen("$ "));

        var effects = Keys(engine, ":");

        Assert.Equal(ModalMode.CommandLine, engine.Mode);
        Assert.Equal(':', engine.CommandPrompt);
        Assert.Equal(":", LastStatus(effects));
        Assert.Equal("Command line", LastSpeech(effects));
    }

    [Fact]
    public void TypingEditsTheLineAndBackspaceRemovesCharacters()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":vsplx");

        Press(engine, Named(KeyName.Backspace));

        Assert.Equal("vspl", engine.CommandText);
    }

    [Fact]
    public void BackspaceOnAnEmptyLineLeavesTheCommandLine()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":");

        var effects = Press(engine, Named(KeyName.Backspace));

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal(ModalMode.Normal, LastMode(effects));
    }

    [Fact]
    public void EscCancelsTheCommandLineWithoutRunningIt()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":q");

        var effects = Press(engine, Esc);

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Null(CommandOf(effects));
    }

    [Fact]
    public void EnterRunsNewWithItsShell()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":new bash -l");

        var effects = Press(engine, Enter);

        Assert.Equal(new ExCommand(ExCommandKind.New, "bash -l"), CommandOf(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal("Opening a new terminal with bash -l.", LastStatus(effects));
    }

    [Fact]
    public void UnknownCommandIsAnErrorAndTheLineStaysForCorrection()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":frobnicate");

        var effects = Press(engine, Enter);

        Assert.Null(CommandOf(effects));
        Assert.Empty(Only<ModalEffect.SendBytes>(effects));
        Assert.Equal("E492: Not an editor command: frobnicate", LastStatus(effects));
        Assert.Equal(ModalMode.CommandLine, engine.Mode);
        Assert.Equal("frobnicate", engine.CommandText);
    }

    [Fact]
    public void EmptyLineReturnsToNormalMode()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":");

        Press(engine, Enter);

        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void LeftRightHomeEndAndDeleteEditInTheMiddle()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":qx");
        Press(engine, Named(KeyName.Left));
        Assert.Equal(1, engine.CommandCursor);

        Press(engine, Named(KeyName.Delete));
        Assert.Equal("q", engine.CommandText);

        Press(engine, Named(KeyName.Home));
        Keys(engine, "!");
        Assert.Equal("!q", engine.CommandText);
        Press(engine, Named(KeyName.End));
        Assert.Equal(2, engine.CommandCursor);
        Press(engine, Named(KeyName.Right));
        Assert.Equal(2, engine.CommandCursor);
    }

    [Fact]
    public void UpAndDownRecallEarlierCommandsAndRestoreTheDraft()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":vsplit");
        Press(engine, Enter);
        Keys(engine, ":clear");
        Press(engine, Enter);

        Keys(engine, ":he");
        Press(engine, Named(KeyName.Up));
        Assert.Equal("clear", engine.CommandText);
        Press(engine, Named(KeyName.Up));
        Assert.Equal("vsplit", engine.CommandText);
        Press(engine, Named(KeyName.Up));
        Assert.Equal("vsplit", engine.CommandText);

        Press(engine, Named(KeyName.Down));
        Assert.Equal("clear", engine.CommandText);
        Press(engine, Named(KeyName.Down));
        Assert.Equal("he", engine.CommandText);
    }

    [Fact]
    public void FailedCommandsAreNotAddedToHistory()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":bogus");
        Press(engine, Enter);
        Press(engine, Esc);

        Keys(engine, ":");
        Press(engine, Named(KeyName.Up));

        Assert.Equal(string.Empty, engine.CommandText);
    }

    [Fact]
    public void QuitAndForceQuitAreCommands()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":q");
        var quit = Press(engine, Enter);
        Keys(engine, ":q!");
        var force = Press(engine, Enter);

        Assert.Equal(new ExCommand(ExCommandKind.Quit), CommandOf(quit));
        Assert.Equal(new ExCommand(ExCommandKind.QuitForce), CommandOf(force));
    }

    [Fact]
    public void SetCarriesTheOptionAndValue()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":set scrollback=5000");

        var effects = Press(engine, Enter);

        Assert.Equal(new ExCommand(ExCommandKind.Set, "scrollback", "5000"), CommandOf(effects));
        Assert.Equal("Setting scrollback to 5000.", LastStatus(effects));
    }

    [Fact]
    public void CrashPreviewCarriesTheNumber()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":crash preview 2");

        var effects = Press(engine, Enter);

        Assert.Equal(new ExCommand(ExCommandKind.CrashPreview, Number: 2), CommandOf(effects));
    }

    [Fact]
    public void AttachWithoutAnIdIsAnError()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":attach");

        var effects = Press(engine, Enter);

        Assert.Null(CommandOf(effects));
        Assert.Equal("attach needs a terminal id.", LastStatus(effects));
        Assert.Equal(ModalMode.CommandLine, engine.Mode);
    }

    [Fact]
    public void SearchPromptDoesNotUseExHistory()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":vsplit");
        Press(engine, Enter);

        Keys(engine, "/");
        Press(engine, Named(KeyName.Up));

        Assert.Equal(string.Empty, engine.CommandText);
    }

    [Fact]
    public void HelpAndThemeTakeOptionalArguments()
    {
        var engine = Engine(Screen("$ "));

        Keys(engine, ":theme dark");
        var theme = Press(engine, Enter);
        Keys(engine, ":help");
        var help = Press(engine, Enter);

        Assert.Equal(new ExCommand(ExCommandKind.Theme, "dark"), CommandOf(theme));
        Assert.Equal(new ExCommand(ExCommandKind.Help), CommandOf(help));
    }

    [Fact]
    public void UnboundMnemonicIsHandedToTheBridge()
    {
        var bridge = new FakeBridge("settings");
        var engine = Engine(Screen("$ "), bridge);

        var effects = Keys(engine, "settings");

        Assert.Equal(new[] { "settings" }, bridge.Handled);
        Assert.Equal("Ran settings", LastStatus(effects));
        Assert.Empty(Only<ModalEffect.SendBytes>(effects));
    }

    [Fact]
    public void ASequenceThatIsAPrefixWaitsForMoreKeys()
    {
        var bridge = new FakeBridge("settings");
        var engine = Engine(Screen("$ "), bridge);

        var waiting = Keys(engine, "set");
        Assert.Empty(bridge.Handled);
        Assert.Null(LastStatus(waiting));

        var done = Keys(engine, "tings");
        Assert.Equal(new[] { "settings" }, bridge.Handled);
        Assert.Equal("Ran settings", LastStatus(done));
    }

    [Fact]
    public void EscCancelsAPendingMnemonic()
    {
        var bridge = new FakeBridge("settings");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, "set");

        Press(engine, Esc);
        var effects = Keys(engine, "x");

        Assert.Empty(bridge.Handled);
        Assert.Equal("No command: x", LastStatus(effects));
    }

    [Fact]
    public void BoundKeysAreMotionsAndNeverReachTheBridge()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);

        var effects = Keys(engine, "n");

        Assert.Empty(bridge.Handled);
        Assert.Equal("No previous search pattern.", LastStatus(effects));
    }

    [Fact]
    public void RemappingAKeyFreesItForAMnemonic()
    {
        var bridge = new FakeBridge("ngc1");
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind('n');
        var engine = new ModalEngine(Screen("$ "), bridge, keymap: keymap);

        Keys(engine, "ngc1");

        Assert.Equal(new[] { "ngc1" }, bridge.Handled);
    }

    [Fact]
    public void OnlyTheFirstKeyOfASequenceIsCheckedAgainstTheKeymap()
    {
        // 's' is unbound, so 'e' and 'i' inside "settings" are typed characters even though they are bound.
        var bridge = new FakeBridge("settings");
        var engine = Engine(Screen("$ "), bridge);

        var effects = Keys(engine, "settings");

        Assert.Equal(new[] { "settings" }, bridge.Handled);
        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal("Ran settings", LastStatus(effects));
    }

    [Fact]
    public void DigitsInsideAMnemonicAreTypedCharacters()
    {
        var bridge = new FakeBridge("gc1");
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind('g');
        var engine = new ModalEngine(Screen("$ "), bridge, keymap: keymap);

        Keys(engine, "gc1");

        Assert.Equal(new[] { "gc1" }, bridge.Handled);
    }

    [Fact]
    public void WithoutABridgeATypedSequenceIsAnError()
    {
        var engine = Engine(Screen("$ "));

        var effects = Keys(engine, "x");

        Assert.Equal("No command: x", LastStatus(effects));
    }

    [Fact]
    public void MnemonicsAreNotStartedInVisualMode()
    {
        var bridge = new FakeBridge("settings");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, "v");

        var effects = Keys(engine, "s");

        Assert.Empty(bridge.Handled);
        Assert.Equal("No command: s", LastStatus(effects));
    }

    [Fact]
    public void PhysicalCtrlLetterDoesNotOpenCalcinatoCommands()
    {
        var engine = Engine(Screen("$ "));

        var effects = Press(engine, TerminalKey.Control(0x1B));

        Assert.Empty(Only<ModalEffect.SendBytes>(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void TypingPastTheLengthLimitIsIgnored()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, ":");
        for (int i = 0; i < 4096; i++)
        {
            Press(engine, TerminalKey.Typed("a"));
        }

        Press(engine, TerminalKey.Typed("b"));

        Assert.Equal(4096, engine.CommandText.Length);
    }
}
