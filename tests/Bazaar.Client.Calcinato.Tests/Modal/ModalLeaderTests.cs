#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;
using static Bazaar.Client.Calcinato.Tests.Modal.Input;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ModalLeaderTests
{
    private static FakeScreen Screen(params string[] visible) =>
        new(rows: Math.Max(1, visible.Length), cols: 40, scrollback: Array.Empty<string>(), visible: visible);

    private static ModalEngine Engine(FakeScreen screen, IMnemonicBridge? mnemonics = null, ModalKeymap? keymap = null) =>
        new(screen, mnemonics, keymap: keymap);

    [Fact]
    public void LeaderStartsAMnemonicWhoseFirstLetterIsAMotion()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);

        var effects = Keys(engine, " ngc1");

        Assert.Equal(new[] { "ngc1" }, bridge.Handled);
        Assert.Equal("Ran ngc1", LastStatus(effects));
        Assert.Empty(Bytes(effects));
        Assert.False(engine.IsLeading);
    }

    [Theory]
    [InlineData("unread")]
    [InlineData("nrfdrm")]
    public void LeaderReachesMnemonicsThatStartWithMotionKeys(string mnemonic)
    {
        var bridge = new FakeBridge(mnemonic);
        var engine = Engine(Screen("$ "), bridge);

        Keys(engine, " " + mnemonic);

        Assert.Equal(new[] { mnemonic }, bridge.Handled);
    }

    [Fact]
    public void WithoutLeaderAMotionKeyStillMovesAndNeverStartsAMnemonic()
    {
        var bridge = new FakeBridge("unread");
        var engine = Engine(Screen("$ "), bridge);

        Keys(engine, "u");

        Assert.Empty(bridge.Handled);
        Assert.False(engine.IsLeading);
    }

    [Fact]
    public void LeaderSequenceWaitsWhileItIsAPrefix()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);

        var waiting = Keys(engine, " ng");

        Assert.True(engine.IsLeading);
        Assert.Empty(bridge.Handled);
        Assert.Equal("Mnemonic: ng", LastStatus(waiting));
    }

    [Fact]
    public void EscCancelsALeaderSequence()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ngc");

        var cancelled = Press(engine, Esc);
        var after = Keys(engine, "x");

        Assert.Empty(bridge.Handled);
        Assert.False(engine.IsLeading);
        Assert.Equal("Mnemonic cancelled.", LastStatus(cancelled));
        Assert.Equal("No command: x", LastStatus(after));
    }

    [Fact]
    public void EscRightAfterTheLeaderCancelsWithoutRunningAnything()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ");

        var effects = Press(engine, Esc);

        Assert.False(engine.IsLeading);
        Assert.Equal("Mnemonic cancelled.", LastStatus(effects));
        Assert.Empty(bridge.Handled);
    }

    [Fact]
    public void EnterRunsAnExactMatchThatIsAlsoAPrefix()
    {
        var bridge = new FakeBridge("ngc", "ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ngc");
        Assert.Empty(bridge.Handled);

        var effects = Press(engine, Enter);

        Assert.Equal(new[] { "ngc" }, bridge.Handled);
        Assert.Equal("Ran ngc", LastStatus(effects));
        Assert.False(engine.IsLeading);
    }

    [Fact]
    public void ANextKeyThatExtendsAnExactPrefixRunsTheLonger()
    {
        var bridge = new FakeBridge("ngc", "ngc1");
        var engine = Engine(Screen("$ "), bridge);

        Keys(engine, " ngc1");

        Assert.Equal(new[] { "ngc1" }, bridge.Handled);
    }

    [Fact]
    public void AKeyThatCannotExtendAnExactPrefixRunsItAndIsDropped()
    {
        var bridge = new FakeBridge("ngc", "ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ngc");

        var effects = Keys(engine, "x");

        Assert.Equal(new[] { "ngc" }, bridge.Handled);
        Assert.Equal("Ran ngc", LastStatus(effects));
        Assert.Empty(Bytes(effects));
        Assert.False(engine.IsLeading);
    }

    [Fact]
    public void EnterOnANonExactPrefixReportsNoCommand()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ngc");

        var effects = Press(engine, Enter);

        Assert.Empty(bridge.Handled);
        Assert.Equal("No command: ngc", LastStatus(effects));
        Assert.False(engine.IsLeading);
    }

    [Fact]
    public void ATypedSequenceThatMatchesNothingEndsTheLeader()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);

        var effects = Keys(engine, " z");

        Assert.Equal("No command: z", LastStatus(effects));
        Assert.False(engine.IsLeading);
        Assert.Empty(bridge.Handled);
    }

    [Fact]
    public void BackspaceRemovesOneKeyFromTheSequence()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ngc");

        Press(engine, Named(KeyName.Backspace));
        var effects = Keys(engine, "c1");

        Assert.Equal(new[] { "ngc1" }, bridge.Handled);
        Assert.Equal("Ran ngc1", LastStatus(effects));
    }

    [Fact]
    public void BackspaceOnAnEmptyLeaderCancels()
    {
        var engine = Engine(Screen("$ "), new FakeBridge("ngc1"));
        Keys(engine, " ");

        var effects = Press(engine, Named(KeyName.Backspace));

        Assert.False(engine.IsLeading);
        Assert.Equal("Mnemonic cancelled.", LastStatus(effects));
    }

    [Fact]
    public void EnterOnAnEmptyLeaderCancelsSilently()
    {
        var engine = Engine(Screen("$ "), new FakeBridge("ngc1"));
        Keys(engine, " ");

        var effects = Press(engine, Enter);

        Assert.False(engine.IsLeading);
        Assert.Equal("Mnemonic cancelled.", LastStatus(effects));
    }

    [Theory]
    [InlineData(KeyName.Left)]
    [InlineData(KeyName.Up)]
    [InlineData(KeyName.PageDown)]
    public void NavigationKeysCancelTheLeader(KeyName name)
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, " ng");

        Press(engine, Named(name));

        Assert.False(engine.IsLeading);
        Assert.Empty(bridge.Handled);
    }

    [Fact]
    public void ControlByteCancelsTheLeader()
    {
        var engine = Engine(Screen("$ "), new FakeBridge("ngc1"));
        Keys(engine, " n");

        Press(engine, TerminalKey.Control(0x03));

        Assert.False(engine.IsLeading);
    }

    [Fact]
    public void LeaderIsNotStartedInVisualMode()
    {
        var bridge = new FakeBridge("ngc1");
        var engine = Engine(Screen("$ "), bridge);
        Keys(engine, "v");

        var effects = Keys(engine, " ");

        Assert.False(engine.IsLeading);
        Assert.Equal("No command:  ", LastStatus(effects));
        Assert.Empty(bridge.Handled);
    }

    [Fact]
    public void BuiltInIntrRunsInsideTheLeaderEvenWhenIIsBound()
    {
        var engine = Engine(Screen("$ "));

        var effects = Keys(engine, " intr");

        Assert.Equal(new byte[] { 0x03 }, Bytes(effects));
        Assert.Equal("Interrupt sent.", LastStatus(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void IntrWithoutLeaderStillEntersInsertBecauseIIsAMotion()
    {
        var engine = Engine(Screen("$ "));

        var effects = Keys(engine, "intr");

        // The i enters Insert, and the rest of the letters go to the shell as text.
        Assert.Equal(ModalMode.Insert, engine.Mode);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("ntr"), Bytes(effects));
    }

    [Fact]
    public void IntrPrefixWaitsInsideTheLeader()
    {
        var engine = Engine(Screen("$ "));

        var waiting = Keys(engine, " i");

        Assert.True(engine.IsLeading);
        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Empty(Bytes(waiting));
    }

    [Fact]
    public void LeaderIsAKeymapSettingAndCanBeRemapped()
    {
        var bridge = new FakeBridge("ngc1");
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind(',');
        keymap.SetLeaderKey(',');
        var engine = Engine(Screen("$ "), bridge, keymap);

        Keys(engine, ",ngc1");
        var spaceEffects = Keys(engine, " ");

        Assert.Equal(new[] { "ngc1" }, bridge.Handled);
        Assert.False(engine.IsLeading);
        Assert.Equal("No command:  ", LastStatus(spaceEffects));
    }

    [Fact]
    public void RemappedLeaderStillLetsMotionsWork()
    {
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind(',');
        keymap.SetLeaderKey(',');
        var screen = Screen("alpha", "beta", "gamma");
        var engine = Engine(screen, keymap: keymap);

        Keys(engine, "j");

        Assert.Equal(1, engine.CursorRow);
    }

    [Fact]
    public void TheDefaultLeaderIsSpace()
    {
        Assert.Equal(' ', ModalKeymap.CreateDefault().LeaderKey);
        Assert.Equal(' ', ModalKeymap.DefaultLeaderKey);
    }

    [Fact]
    public void LeaderCannotBeABoundMotion()
    {
        var keymap = ModalKeymap.CreateDefault();

        Assert.Throws<ArgumentException>(() => keymap.SetLeaderKey('h'));
        Assert.Equal(' ', keymap.LeaderKey);
    }

    [Theory]
    [InlineData('1')]
    [InlineData('0')]
    [InlineData('\n')]
    [InlineData('é')]
    public void LeaderCannotBeADigitOrANonPrintableCharacter(char key)
    {
        Assert.Throws<ArgumentException>(() => new ModalKeymap().SetLeaderKey(key));
    }

    [Fact]
    public void BindingTheLeaderKeyFails()
    {
        var keymap = new ModalKeymap();
        keymap.SetLeaderKey(',');

        Assert.Throws<ArgumentException>(() => keymap.Bind(',', ModalAction.MoveLeft));
    }

    [Fact]
    public void CloneKeepsTheLeader()
    {
        var keymap = ModalKeymap.CreateDefault();
        keymap.Unbind(',');
        keymap.SetLeaderKey(',');

        var copy = keymap.Clone();

        Assert.Equal(',', copy.LeaderKey);
    }
}
