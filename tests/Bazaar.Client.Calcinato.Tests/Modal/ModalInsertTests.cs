#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using System.Text;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;
using static Bazaar.Client.Calcinato.Tests.Modal.Input;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ModalInsertTests
{
    private static FakeScreen Screen(params string[] visible) =>
        new(rows: Math.Max(1, visible.Length), cols: 40, scrollback: Array.Empty<string>(), visible: visible);

    private static ModalEngine Engine(FakeScreen screen, bool appCursor = false, bool jk = false, ISuggestionSource? suggestions = null, IMnemonicBridge? mnemonics = null) =>
        new(screen, mnemonics, suggestions, jk, () => appCursor);

    private static byte[] Seq(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void TypedTextIsSentAsUtf8()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Keys(engine, "ls");

        Assert.Equal(Encoding.UTF8.GetBytes("ls"), Bytes(effects));
    }

    [Fact]
    public void MultiByteCharactersAreUtf8Encoded()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Press(engine, TerminalKey.Typed("é"));

        Assert.Equal(new byte[] { 0xC3, 0xA9 }, Bytes(effects));
    }

    [Fact]
    public void MultiCharacterTextIsSentWhole()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Press(engine, TerminalKey.Typed("abc"));

        Assert.Equal(Encoding.UTF8.GetBytes("abc"), Bytes(effects));
    }

    [Theory]
    [InlineData(KeyName.Enter, new byte[] { 0x0D })]
    [InlineData(KeyName.Backspace, new byte[] { 0x7F })]
    [InlineData(KeyName.Tab, new byte[] { 0x09 })]
    public void EditingKeysAreSentAsTheirControlBytes(KeyName key, byte[] expected)
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Press(engine, Named(key));

        Assert.Equal(expected, Bytes(effects));
    }

    [Fact]
    public void ShiftTabIsBackTab()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Press(engine, Named(KeyName.Tab, shift: true));

        Assert.Equal(Seq("\u001b[Z"), Bytes(effects));
    }

    [Fact]
    public void EscapeReturnsToNormalAndIsNotSentToTheShell()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Press(engine, Esc);

        Assert.Empty(Bytes(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void PhysicalCtrlCPassesThroughInInsertMode()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Press(engine, TerminalKey.Control(0x03));

        Assert.Equal(new byte[] { 0x03 }, Bytes(effects));
        Assert.Equal(ModalMode.Insert, engine.Mode);
    }

    [Fact]
    public void RawControlInNormalModeSendsNothing()
    {
        var engine = Engine(Screen("$ "));

        var effects = Press(engine, TerminalKey.Control(0x03));

        Assert.Empty(Bytes(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Contains("Insert mode", LastStatus(effects));
    }

    [Theory]
    [InlineData(false, "\u001b[A")]
    [InlineData(true, "\u001bOA")]
    public void UpArrowFollowsApplicationCursorKeys(bool app, string expected)
    {
        var engine = Engine(Screen("$ "), appCursor: app);
        Keys(engine, "i");

        var effects = Press(engine, Named(KeyName.Up));

        Assert.Equal(Seq(expected), Bytes(effects));
    }

    [Theory]
    [InlineData(false, "\u001b[H", "\u001b[F")]
    [InlineData(true, "\u001bOH", "\u001bOF")]
    public void HomeAndEndFollowApplicationCursorKeys(bool app, string home, string end)
    {
        var engine = Engine(Screen("$ "), appCursor: app);
        Keys(engine, "i");

        Assert.Equal(Seq(home), Bytes(Press(engine, Named(KeyName.Home))));
        Assert.Equal(Seq(end), Bytes(Press(engine, Named(KeyName.End))));
    }

    [Fact]
    public void ApplicationCursorFlagIsReadEveryKeyPress()
    {
        bool app = false;
        var engine = new ModalEngine(Screen("$ "), applicationCursorKeys: () => app);
        Keys(engine, "i");

        Assert.Equal(Seq("\u001b[B"), Bytes(Press(engine, Named(KeyName.Down))));
        app = true;
        Assert.Equal(Seq("\u001bOB"), Bytes(Press(engine, Named(KeyName.Down))));
    }

    [Fact]
    public void ShiftedArrowsAlwaysUseModifierForm()
    {
        var engine = Engine(Screen("$ "), appCursor: true);
        Keys(engine, "i");

        Assert.Equal(Seq("\u001b[1;2C"), Bytes(Press(engine, Named(KeyName.Right, shift: true))));
    }

    [Fact]
    public void PageKeysAreNotAffectedByApplicationCursorMode()
    {
        var engine = Engine(Screen("$ "), appCursor: true);
        Keys(engine, "i");

        Assert.Equal(Seq("\u001b[5~"), Bytes(Press(engine, Named(KeyName.PageUp))));
        Assert.Equal(Seq("\u001b[6~"), Bytes(Press(engine, Named(KeyName.PageDown))));
    }

    [Fact]
    public void ReadOnlySessionRefusesInsertWithAMessage()
    {
        var engine = Engine(Screen("$ "));
        engine.CanWrite = false;

        var effects = Keys(engine, "i");

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Null(LastMode(effects));
        Assert.Equal("Read-only session: Insert mode is not available.", LastStatus(effects));
    }

    [Fact]
    public void ReadOnlySessionKeepsNormalModeNavigation()
    {
        var engine = Engine(Screen("abc", "def"));
        engine.CanWrite = false;

        Keys(engine, "jl");

        Assert.Equal((1, 1), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void ReadOnlySessionRefusesInterruptButAllowsQuit()
    {
        var engine = Engine(Screen("$ "));
        engine.CanWrite = false;

        Keys(engine, ":interrupt");
        var refused = Press(engine, Enter);
        Press(engine, Esc);
        Keys(engine, ":q");
        var quit = Press(engine, Enter);

        Assert.Empty(Bytes(refused));
        Assert.Equal("Read-only session: input is not sent.", LastStatus(refused));
        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal(new ExCommand(ExCommandKind.Quit), CommandOf(quit));
    }

    [Fact]
    public void LosingWriteAccessWhileInsertSwitchesToNormalOnTheNextKey()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");
        engine.CanWrite = false;

        var effects = Press(engine, TerminalKey.Typed("x"));

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Empty(Bytes(effects));
        Assert.Equal(ModalMode.Normal, LastMode(effects));
    }

    [Fact]
    public void ReadOnlySessionRefusesTheIntrMnemonic()
    {
        var screen = Screen("$ ");
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind('i');
        var engine = new ModalEngine(screen, keymap: keymap) { CanWrite = false };

        var effects = Keys(engine, "intr");

        Assert.Empty(Bytes(effects));
        Assert.Equal("Read-only session: input is not sent.", LastStatus(effects));
    }

    [Theory]
    [InlineData(":interrupt", new byte[] { 0x03 })]
    [InlineData(":eof", new byte[] { 0x04 })]
    [InlineData(":suspend", new byte[] { 0x1A })]
    public void InterruptEofAndSuspendCommandsSendControlBytes(string command, byte[] expected)
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, command);

        var effects = Press(engine, Enter);

        Assert.Equal(expected, Bytes(effects));
        Assert.Null(CommandOf(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void IntrMnemonicSendsInterruptWhenItsKeyIsFree()
    {
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind('i');
        var engine = new ModalEngine(Screen("$ "), keymap: keymap);

        var effects = Keys(engine, "intr");

        Assert.Equal(new byte[] { 0x03 }, Bytes(effects));
    }

    [Fact]
    public void JkEscapeIsOffByDefault()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");

        var effects = Keys(engine, "jk");

        Assert.Equal(ModalMode.Insert, engine.Mode);
        Assert.Equal(Encoding.UTF8.GetBytes("jk"), Bytes(effects));
    }

    [Fact]
    public void JkEscapeLeavesInsertWithoutSendingEither()
    {
        var engine = Engine(Screen("$ "), jk: true);
        Keys(engine, "i");

        var effects = Keys(engine, "jk");

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Empty(Bytes(effects));
    }

    [Fact]
    public void AHeldJIsSentWhenTheNextKeyIsNotK()
    {
        var engine = Engine(Screen("$ "), jk: true);
        Keys(engine, "i");

        var effects = Keys(engine, "jx");

        Assert.Equal(ModalMode.Insert, engine.Mode);
        Assert.Equal(Encoding.UTF8.GetBytes("jx"), Bytes(effects));
    }

    [Fact]
    public void EnteringInsertFollowsTheLiveCursor()
    {
        var screen = new FakeScreen(3, 40, new[] { "old" }, new[] { "a", "$ ", "" })
        {
            CursorRow = 1,
            CursorCol = 2,
        };
        var engine = Engine(screen);
        Keys(engine, "gg");
        Assert.Equal(0, engine.CursorRow);

        Keys(engine, "i");

        Assert.Equal(2, engine.CursorRow);
        Assert.Equal(2, engine.CursorColumn);
        Assert.Equal(1, engine.TopRow);
    }

    [Fact]
    public async Task SuggestionsAreOnlyGhostTextUntilAcceptedWithTab()
    {
        var suggestions = new FakeSuggestions { Answer = "status" };
        var engine = Engine(Screen("$ "), suggestions: suggestions);
        Keys(engine, "i");
        Keys(engine, "git");

        var refresh = await engine.RefreshSuggestionAsync(CancellationToken.None);

        Assert.Equal(new[] { "git" }, suggestions.Asked);
        Assert.Equal(new ModalEffect.GhostText("status"), Assert.Single(refresh.Effects));
        Assert.Equal("status", engine.GhostSuggestion);
        Assert.Empty(Bytes(refresh.Effects));

        var accepted = Press(engine, Named(KeyName.Tab));

        Assert.Equal(Encoding.UTF8.GetBytes("status"), Bytes(accepted));
        Assert.DoesNotContain(Bytes(accepted), b => b == 0x0D);
        Assert.Contains(accepted, e => e is ModalEffect.GhostText { Text: null });
        Assert.Null(engine.GhostSuggestion);
    }

    [Fact]
    public void TabWithoutGhostTextSendsTab()
    {
        var engine = Engine(Screen("$ "), suggestions: new FakeSuggestions { Answer = null });
        Keys(engine, "i");

        var effects = Press(engine, Named(KeyName.Tab));

        Assert.Equal(new byte[] { 0x09 }, Bytes(effects));
    }

    [Fact]
    public async Task ShiftTabDoesNotAcceptGhostText()
    {
        var engine = Engine(Screen("$ "), suggestions: new FakeSuggestions { Answer = "x" });
        Keys(engine, "i");
        Keys(engine, "a");
        await engine.RefreshSuggestionAsync(CancellationToken.None);

        var effects = Press(engine, Named(KeyName.Tab, shift: true));

        Assert.Equal(Seq("\u001b[Z"), Bytes(effects));
    }

    [Fact]
    public async Task TypingClearsGhostText()
    {
        var engine = Engine(Screen("$ "), suggestions: new FakeSuggestions { Answer = "tatus" });
        Keys(engine, "i");
        Keys(engine, "git");
        await engine.RefreshSuggestionAsync(CancellationToken.None);

        var effects = Keys(engine, "s");

        Assert.Contains(effects, e => e is ModalEffect.GhostText { Text: null });
        Assert.Null(engine.GhostSuggestion);
    }

    [Fact]
    public async Task NoSuggestionSourceReturnsNothing()
    {
        var engine = Engine(Screen("$ "));
        Keys(engine, "i");
        Keys(engine, "git");

        var result = await engine.RefreshSuggestionAsync(CancellationToken.None);

        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task SuggestionsAreNotRequestedOutsideInsertMode()
    {
        var suggestions = new FakeSuggestions { Answer = "x" };
        var engine = Engine(Screen("$ "), suggestions: suggestions);
        Keys(engine, "i");
        Keys(engine, "a");
        Press(engine, Esc);

        var result = await engine.RefreshSuggestionAsync(CancellationToken.None);

        Assert.Empty(result.Effects);
        Assert.Empty(suggestions.Asked);
    }

    [Fact]
    public async Task StaleSuggestionIsDroppedWhenTheLineChangedWhileWaiting()
    {
        var suggestions = new FakeSuggestions { Answer = "old" };
        var engine = Engine(Screen("$ "), suggestions: suggestions);
        Keys(engine, "i");
        Keys(engine, "gi");
        suggestions.BeforeAnswer = () => Keys(engine, "t");

        var result = await engine.RefreshSuggestionAsync(CancellationToken.None);

        Assert.Empty(result.Effects);
        Assert.Null(engine.GhostSuggestion);
    }

    [Fact]
    public async Task EmptyLineAsksNothingAndClearsGhost()
    {
        var suggestions = new FakeSuggestions { Answer = "ls" };
        var engine = Engine(Screen("$ "), suggestions: suggestions);
        Keys(engine, "i");

        await engine.RefreshSuggestionAsync(CancellationToken.None);

        Assert.Empty(suggestions.Asked);
    }
}
