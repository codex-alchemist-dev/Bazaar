#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;
using static Bazaar.Client.Calcinato.Tests.Modal.Input;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ModalNavigationTests
{
    private static FakeScreen Screen(params string[] visible) =>
        new(rows: Math.Max(1, visible.Length), cols: 40, scrollback: Array.Empty<string>(), visible: visible);

    private static ModalEngine Engine(FakeScreen screen) => new(screen);

    [Fact]
    public void StartsInNormalModeOnTheLiveCursorWithTheViewOnTheBottom()
    {
        var screen = new FakeScreen(5, 40, new[] { "old 1", "old 2" }, new[] { "prompt$ ", "" })
        {
            CursorRow = 1,
            CursorCol = 3,
        };
        var engine = Engine(screen);

        Assert.Equal(ModalMode.Normal, engine.Mode);
        Assert.Equal(3, engine.CursorRow);
        Assert.Equal(3, engine.CursorColumn);
        Assert.Equal(2, engine.TopRow);
    }

    [Fact]
    public void HAndLMoveAndClampAtTheLineEnds()
    {
        var engine = Engine(Screen("abc"));

        Assert.Empty(Keys(engine, "h"));
        Assert.Equal(0, engine.CursorColumn);
        Keys(engine, "ll");
        Assert.Equal(2, engine.CursorColumn);
        Keys(engine, "llll");
        Assert.Equal(2, engine.CursorColumn);
    }

    [Fact]
    public void JAndKClampRowsAndTheColumnToTheShorterLine()
    {
        var engine = Engine(Screen("abcdef", "ab", "abcdef"));

        Keys(engine, "$");
        Assert.Equal(5, engine.CursorColumn);
        Keys(engine, "j");
        Assert.Equal(1, engine.CursorRow);
        Assert.Equal(1, engine.CursorColumn);
        Keys(engine, "kk");
        Assert.Equal(0, engine.CursorRow);
        Keys(engine, "jjjjj");
        Assert.Equal(2, engine.CursorRow);
    }

    [Fact]
    public void CountsRepeatMotions()
    {
        var engine = Engine(Screen("a", "b", "c", "d", "e"));

        Keys(engine, "3j");
        Assert.Equal(3, engine.CursorRow);
        Keys(engine, "10k");
        Assert.Equal(0, engine.CursorRow);
    }

    [Fact]
    public void ZeroIsLineStartButInsideACountItIsADigit()
    {
        var engine = Engine(Screen("abcdefghijk"));

        Keys(engine, "$");
        Assert.Equal(10, engine.CursorColumn);
        Keys(engine, "0");
        Assert.Equal(0, engine.CursorColumn);
        Keys(engine, "10l");
        Assert.Equal(10, engine.CursorColumn);
    }

    [Fact]
    public void DollarMovesToTheLastCharacter()
    {
        var engine = Engine(Screen("hello"));

        Keys(engine, "$");

        Assert.Equal(4, engine.CursorColumn);
    }

    [Fact]
    public void GgAndGJumpToTheTopAndBottom()
    {
        var engine = Engine(Screen("a", "b", "c", "d", "e"));

        Keys(engine, "G");
        Assert.Equal(4, engine.CursorRow);
        Keys(engine, "gg");
        Assert.Equal(0, engine.CursorRow);
    }

    [Fact]
    public void CountedGgAndGGoToThatLine()
    {
        var engine = Engine(Screen("a", "b", "c", "d", "e"));

        Keys(engine, "3G");
        Assert.Equal(2, engine.CursorRow);
        Keys(engine, "5gg");
        Assert.Equal(4, engine.CursorRow);
        Keys(engine, "1gg");
        Assert.Equal(0, engine.CursorRow);
    }

    [Fact]
    public void GFollowedByAnotherKeyIsAnError()
    {
        var engine = Engine(Screen("a"));

        var effects = Keys(engine, "gx");

        Assert.Equal("No command: gx", LastStatus(effects));
    }

    [Fact]
    public void WordMotionsWalkOneLine()
    {
        var engine = Engine(Screen("foo bar baz"));

        Keys(engine, "w");
        Assert.Equal(4, engine.CursorColumn);
        Keys(engine, "w");
        Assert.Equal(8, engine.CursorColumn);
        Keys(engine, "b");
        Assert.Equal(4, engine.CursorColumn);
        Keys(engine, "e");
        Assert.Equal(6, engine.CursorColumn);
        Keys(engine, "e");
        Assert.Equal(10, engine.CursorColumn);
    }

    [Fact]
    public void WordForwardCrossesLinesAndStopsOnAnEmptyLine()
    {
        var engine = Engine(Screen("ab", "", "cd"));

        Keys(engine, "w");
        Assert.Equal((1, 0), (engine.CursorRow, engine.CursorColumn));
        Keys(engine, "w");
        Assert.Equal((2, 0), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void WordBackwardStopsOnAnEmptyLine()
    {
        var engine = Engine(Screen("ab", "", "cd"));
        Keys(engine, "G");

        Keys(engine, "b");

        Assert.Equal((1, 0), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void PunctuationIsItsOwnWordClass()
    {
        var engine = Engine(Screen("a.b c"));

        Keys(engine, "w");
        Assert.Equal(1, engine.CursorColumn);
        Keys(engine, "w");
        Assert.Equal(2, engine.CursorColumn);
    }

    [Fact]
    public void CursorMovesEmitCursorMovedAndScrollingEmitsScrollTo()
    {
        var screen = new FakeScreen(2, 40, new[] { "s1", "s2", "s3" }, new[] { "v1", "v2" });
        var engine = Engine(screen);

        // The live cursor starts on absolute row 3 (the first visible row). The view starts at the bottom, at row 2.
        var effects = Keys(engine, "k");
        Assert.Contains(effects, e => e is ModalEffect.CursorMoved { Row: 2, Column: 0 });
        Assert.Contains(effects, e => e is ModalEffect.ScrollTo { TopRow: 2 });

        effects = Keys(engine, "kk");
        Assert.Contains(effects, e => e is ModalEffect.ScrollTo { TopRow: 0 });
        Assert.Equal(0, engine.TopRow);
        Assert.Equal(0, engine.CursorRow);
    }

    [Fact]
    public void PagingKeysMoveByTheScreenHeight()
    {
        var scrollback = Enumerable.Range(0, 20).Select(i => $"line {i}").ToArray();
        var screen = new FakeScreen(5, 40, scrollback, new[] { "a", "b", "c", "d", "e" });
        var engine = Engine(screen);
        Assert.Equal(20, engine.CursorRow);
        Assert.Equal(20, engine.TopRow);

        Keys(engine, "B");
        Assert.Equal(15, engine.CursorRow);
        Assert.Equal(15, engine.TopRow);

        Keys(engine, "f");
        Assert.Equal(20, engine.CursorRow);
        Assert.Equal(16, engine.TopRow);
    }

    [Fact]
    public void HalfPagesAndNamedPageKeys()
    {
        var scrollback = Enumerable.Range(0, 20).Select(i => $"line {i}").ToArray();
        var engine = Engine(new FakeScreen(4, 40, scrollback, new[] { "a", "b", "c", "d" }));

        Keys(engine, "u");
        Assert.Equal(18, engine.CursorRow);
        Keys(engine, "d");
        Assert.Equal(20, engine.CursorRow);
        Press(engine, Named(KeyName.PageUp));
        Assert.Equal(16, engine.CursorRow);
        Press(engine, Named(KeyName.PageDown));
        Assert.Equal(20, engine.CursorRow);
        Press(engine, Named(KeyName.Home));
        Assert.Equal(0, engine.CursorRow);
        Press(engine, Named(KeyName.End));
        Assert.Equal(23, engine.CursorRow);
    }

    [Fact]
    public void ArrowKeysMoveLikeHjkl()
    {
        var engine = Engine(Screen("abc", "def"));

        Press(engine, Named(KeyName.Right));
        Press(engine, Named(KeyName.Down));
        Assert.Equal((1, 1), (engine.CursorRow, engine.CursorColumn));
        Press(engine, Named(KeyName.Left));
        Press(engine, Named(KeyName.Up));
        Assert.Equal((0, 0), (engine.CursorRow, engine.CursorColumn));
    }

    [Fact]
    public void UnboundCharacterWithNoMnemonicBridgeIsAnError()
    {
        var engine = Engine(Screen("a"));

        var effects = Keys(engine, "x");

        Assert.Equal("No command: x", LastStatus(effects));
        Assert.Equal(ModalMode.Normal, engine.Mode);
    }

    [Fact]
    public void ScrollbackGrowthKeepsTheCursorOnTheSameText()
    {
        var screen = new FakeScreen(2, 40, Array.Empty<string>(), new[] { "one", "two" });
        var engine = Engine(screen);
        Keys(engine, "j");
        Assert.Equal(1, engine.CursorRow);

        screen.Scroll("zero");
        Keys(engine, "k");

        Assert.Equal("one", screen.GetLineText(engine.CursorRow));
    }
}
