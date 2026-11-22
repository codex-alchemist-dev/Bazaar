#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;
using static Bazaar.Client.Calcinato.Tests.Modal.Input;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ModalSearchAndModeTests
{
    private static FakeScreen Screen(params string[] visible) =>
        new(rows: visible.Length, cols: 40, scrollback: Array.Empty<string>(), visible: visible);

    private static ModalEngine Engine(FakeScreen screen) => new(screen);

    [Fact]
    public void ForwardSearchMovesToTheNextMatchAfterTheCursor()
    {
        var engine = Engine(Screen("alpha", "beta alpha", "gamma"));

        Keys(engine, "/alpha");
        Press(engine, Enter);

        Assert.Equal((1, 5), (engine.CursorRow, engine.CursorColumn));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void NAndCapitalNRepeatTheSearchInEitherDirection()
    {
        var engine = Engine(Screen("alpha", "beta alpha", "gamma"));
        Keys(engine, "/alpha");
        Press(engine, Enter);

        Keys(engine, "n");
        Assert.Equal((0, 0), (engine.CursorRow, engine.CursorColumn));
        Keys(engine, "N");
        Assert.Equal((1, 5), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void BackwardSearchUsesQuestionMark()
    {
        var engine = Engine(Screen("alpha", "beta alpha", "gamma"));
        Keys(engine, "G");

        Keys(engine, "?alpha");
        Press(engine, Enter);

        Assert.Equal((1, 5), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void SearchWrapsAroundTheBuffer()
    {
        var engine = Engine(Screen("x", "alpha", "y"));
        Keys(engine, "G");

        Keys(engine, "/alpha");
        var effects = Press(engine, Enter);

        Assert.Equal(1, engine.CursorRow);
        Assert.Equal("/alpha", LastStatus(effects));
    }

    [Fact]
    public void SearchSkipsTheMatchUnderTheCursor()
    {
        var engine = Engine(Screen("aa"));

        Keys(engine, "/a");
        Press(engine, Enter);

        Assert.Equal(1, engine.CursorColumn);
    }

    [Fact]
    public void SearchCoversScrollback()
    {
        var screen = new FakeScreen(2, 40, new[] { "needle here", "other" }, new[] { "x", "y" });
        var engine = Engine(screen);

        Keys(engine, "/needle");
        Press(engine, Enter);

        Assert.Equal((0, 0), (engine.CursorRow, engine.CursorColumn));
        Assert.Equal(0, engine.TopRow);
    }

    [Fact]
    public void NotFoundIsAnErrorAndTheCursorStays()
    {
        var engine = Engine(Screen("alpha"));
        Keys(engine, "l");

        Keys(engine, "/zzz");
        var effects = Press(engine, Enter);

        Assert.Equal("Pattern not found: zzz", LastStatus(effects));
        Assert.Equal((0, 1), (engine.CursorRow, engine.CursorColumn));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void EmptyPatternRepeatsTheLastSearch()
    {
        var engine = Engine(Screen("ab ab ab"));
        Keys(engine, "/ab");
        Press(engine, Enter);
        Assert.Equal(3, engine.CursorColumn);

        Keys(engine, "/");
        Press(engine, Enter);

        Assert.Equal(6, engine.CursorColumn);
    }

    [Fact]
    public void RepeatingWithoutAnyPatternIsAnError()
    {
        var engine = Engine(Screen("a"));

        var effects = Keys(engine, "n");

        Assert.Equal("No previous search pattern.", LastStatus(effects));
    }

    [Fact]
    public void EscCancelsASearchPrompt()
    {
        var engine = Engine(Screen("alpha"));
        Keys(engine, "/al");

        var effects = Press(engine, Esc);

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal(ModalMode.Normal, LastMode(effects));
        Assert.Equal(0, engine.CursorColumn);
    }

    [Fact]
    public void MarksSaveAndRecallPositions()
    {
        var engine = Engine(Screen("abcdef", "ghi"));
        Keys(engine, "lll");
        Keys(engine, "ma");
        Keys(engine, "jjj");
        Assert.Equal(1, engine.CursorRow);

        Keys(engine, "'a");

        Assert.Equal((0, 3), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void JumpingToAnUnsetMarkIsAnError()
    {
        var engine = Engine(Screen("a"));

        var effects = Keys(engine, "'b");

        Assert.Equal("Mark b is not set.", LastStatus(effects));
    }

    [Fact]
    public void MarkNamesMustBeLowercaseLetters()
    {
        var engine = Engine(Screen("a"));

        var effects = Keys(engine, "mA");

        Assert.Equal("Marks are lowercase letters a to z.", LastStatus(effects));
    }

    [Fact]
    public void MarksFollowScrollbackGrowth()
    {
        var screen = new FakeScreen(2, 40, Array.Empty<string>(), new[] { "one", "two" });
        var engine = Engine(screen);
        Keys(engine, "ma");
        screen.Scroll("zero");

        Keys(engine, "'a");

        Assert.Equal("one", screen.GetLineText(engine.CursorRow));
    }

    [Fact]
    public void IEntersInsertModeAndEscReturnsToNormal()
    {
        var engine = Engine(Screen("prompt$"));

        var entered = Keys(engine, "i");
        Assert.Equal(ModalMode.Insert, engine.Mode);
        Assert.Equal(ModalMode.Insert, LastMode(entered));
        Assert.Equal("Insert mode", LastSpeech(entered));

        var left = Press(engine, Esc);
        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal(ModalMode.Normal, LastMode(left));
        Assert.Equal("Normal mode", LastSpeech(left));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("I")]
    [InlineData("A")]
    public void OtherInsertKeysAlsoEnterInsertMode(string key)
    {
        var engine = Engine(Screen("prompt$"));

        Keys(engine, key);

        Assert.Equal(ModalMode.Insert, engine.Mode);
    }

    [Fact]
    public void InsertKeyEntersInsertMode()
    {
        var engine = Engine(Screen("prompt$"));

        Press(engine, Named(KeyName.Insert));

        Assert.Equal(ModalMode.Insert, engine.Mode);
    }

    [Fact]
    public void VAndVCycleVisualModes()
    {
        var engine = Engine(Screen("abc", "def"));

        var first = Keys(engine, "v");
        Assert.Equal(ModalMode.VisualChar, engine.Mode);
        Assert.Equal("Visual mode, characters", LastSpeech(first));

        Keys(engine, "V");
        Assert.Equal(ModalMode.VisualLine, engine.Mode);

        Keys(engine, "V");
        Assert.Equal(ModalMode.Normal, engine.Mode);

        Keys(engine, "v");
        Keys(engine, "v");
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void EscLeavesVisualModeWithoutYanking()
    {
        var engine = Engine(Screen("abc"));
        Keys(engine, "vl");

        var effects = Press(engine, Esc);

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Null(YankOf(effects));
    }

    [Fact]
    public void VisualCharacterYankCopiesTheSelectionInclusively()
    {
        var engine = Engine(Screen("hello world"));
        Keys(engine, "vll");

        var effects = Keys(engine, "y");

        Assert.Equal("hel", YankOf(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void VisualYankWorksBackwardsToo()
    {
        var engine = Engine(Screen("hello"));
        Keys(engine, "$");
        Keys(engine, "vhh");

        var effects = Keys(engine, "y");

        Assert.Equal("llo", YankOf(effects));
    }

    [Fact]
    public void MultiLineCharacterYankJoinsWithNewlines()
    {
        var engine = Engine(Screen("abc", "def", "ghi"));
        Keys(engine, "l");
        Keys(engine, "vj");

        var effects = Keys(engine, "y");

        Assert.Equal("bc\nde", YankOf(effects));
    }

    [Fact]
    public void VisualLineYankCopiesWholeRows()
    {
        var engine = Engine(Screen("abc", "def", "ghi"));
        Keys(engine, "Vj");

        var effects = Keys(engine, "y");

        Assert.Equal("abc\ndef", YankOf(effects));
    }

    [Fact]
    public void YankOutsideVisualModeIsAnError()
    {
        var engine = Engine(Screen("abc"));

        var effects = Keys(engine, "y");

        Assert.Null(YankOf(effects));
        Assert.Equal("Yank works in Visual mode. Press v or V first.", LastStatus(effects));
    }

    [Fact]
    public void ModeChangesAreSpokenForEveryTransition()
    {
        var engine = Engine(Screen("abc"));

        var toVisual = Keys(engine, "v");
        var toCommand = Keys(engine, ":");

        Assert.Equal("Visual mode, characters", LastSpeech(toVisual));
        Assert.Equal("Command line", LastSpeech(toCommand));
    }

    [Fact]
    public void CommandLineStartedFromVisualReturnsToVisual()
    {
        var engine = Engine(Screen("abc"));
        Keys(engine, "v");
        Keys(engine, ":");

        Press(engine, Esc);

        Assert.Equal(ModalMode.VisualChar, engine.Mode);
    }

    [Fact]
    public void InsertIsRefusedInVisualMode()
    {
        var engine = Engine(Screen("abc"));
        Keys(engine, "v");

        var effects = Keys(engine, "i");

        Assert.Equal(ModalMode.VisualChar, engine.Mode);
        Assert.Equal("Insert is not available in Visual mode. Press Escape first.", LastStatus(effects));
    }

    [Fact]
    public void CountedSearchRepeatsTheMatchJump()
    {
        var engine = Engine(Screen("a x a x a x a"));
        Keys(engine, "/a");
        Press(engine, Enter);
        Assert.Equal(4, engine.CursorColumn);

        Keys(engine, "2n");

        Assert.Equal(12, engine.CursorColumn);
    }
}
