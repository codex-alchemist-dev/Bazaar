using Bazaar.Client.Calcinato.Emulation;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Terminal.Calcinato;

namespace Bazaar.Client.Terminal.Tests.Calcinato;

public sealed class AnsiScreenRendererTests
{
    private const string _enter = "\u001b[?1049h\u001b[?25l\u001b[0m\u001b[2J\u001b[H";
    private const string _leave = "\u001b[0m\u001b[?25h\u001b[?1049l";
    private const string _fullStart = "\u001b[0m\u001b[2J\u001b[H";
    private const string _reset = "\u001b[0m";
    private const string _red = "\u001b[0;31m";
    private const string _statusSgr = "\u001b[0;7m";
    private const string _showCursor = "\u001b[?25h";
    private const string _hideCursor = "\u001b[?25l";

    private static readonly CellStyle _plain = CellStyle.Plain;
    private static readonly CellStyle _red1 = new(TermColor.FromIndex(1), TermColor.Default, CellAttributes.None);

    private static TerminalRow Row(params (string Text, CellStyle Style)[] runs) =>
        new(runs.Select(run => new TerminalCellRun(run.Text, run.Style)).ToArray());

    private static TerminalPaneSnapshot Snap(
        IReadOnlyList<TerminalRow> rows,
        int cursorRow = -1,
        int cursorColumn = 0,
        string mode = "NORMAL",
        string status = "ok",
        bool cursorVisible = true) =>
        TerminalPaneSnapshot.Empty with
        {
            HasSession = true,
            Rows = rows,
            CursorRow = cursorRow,
            CursorColumn = cursorColumn,
            ModeName = mode,
            StatusLine = status,
            CursorVisible = cursorVisible,
        };

    [Fact]
    public void EnterAndLeaveSwitchScreensAndRestoreTheCursor()
    {
        var renderer = new AnsiScreenRenderer();

        Assert.Equal(_enter, renderer.EnterScreen());
        Assert.Equal(_leave, renderer.LeaveScreen());
    }

    [Fact]
    public void FirstFrameIsAGoldenFullDraw()
    {
        var renderer = new AnsiScreenRenderer();
        var snapshot = Snap(
            [Row(("hi", _plain)), Row(("red", _red1))],
            cursorRow: 0,
            cursorColumn: 2);

        string frame = renderer.Render(snapshot, 10, 3);

        string expected = _fullStart
            + "\u001b[1;1Hhi        "
            + "\u001b[2;1H" + _red + "red" + _reset + "       "
            + "\u001b[3;1H" + _statusSgr + " NORMAL  o"
            + _reset
            + "\u001b[1;3H" + _showCursor;
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void IdenticalFrameProducesNoOutput()
    {
        var renderer = new AnsiScreenRenderer();
        var snapshot = Snap([Row(("same", _plain))], cursorRow: 0, cursorColumn: 0);
        renderer.Render(snapshot, 8, 2);

        Assert.Equal(string.Empty, renderer.Render(snapshot, 8, 2));
    }

    [Fact]
    public void ChangedRowIsRedrawnAloneAndCursorIsPlacedLast()
    {
        var renderer = new AnsiScreenRenderer();
        renderer.Render(Snap([Row(("aaa", _plain)), Row(("bbb", _plain))], cursorRow: 0, cursorColumn: 0), 6, 3);

        string frame = renderer.Render(Snap([Row(("aaa", _plain)), Row(("bXb", _plain))], cursorRow: 1, cursorColumn: 1), 6, 3);

        Assert.Equal("\u001b[2;1HbXb   \u001b[2;2H", frame);
    }

    [Fact]
    public void SgrIsWrittenOnlyWhenTheStyleChanges()
    {
        var renderer = new AnsiScreenRenderer();
        var snapshot = Snap([Row(("abc", _red1), ("de", _red1), ("f", _plain))]);

        string frame = renderer.Render(snapshot, 6, 2);

        Assert.Equal(1, CountOf(frame, _red));
        Assert.Contains("\u001b[1;1H" + _red + "abcde" + _reset + "f", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributesAndColourDepthsAreEmitted()
    {
        var renderer = new AnsiScreenRenderer();
        var bright = new CellStyle(
            TermColor.FromIndex(9),
            TermColor.FromRgb(1, 2, 3),
            CellAttributes.Bold | CellAttributes.Underline);
        var indexed = new CellStyle(TermColor.FromIndex(200), TermColor.FromIndex(4), CellAttributes.None);

        string frame = renderer.Render(Snap([Row(("s", bright), ("x", indexed))]), 4, 2);

        Assert.Contains("\u001b[0;1;4;91;48;2;1;2;3ms", frame, StringComparison.Ordinal);
        Assert.Contains("\u001b[0;38;5;200;44mx", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void WideCharactersTakeTwoColumnsAndAreNotSplit()
    {
        var renderer = new AnsiScreenRenderer();

        string frame = renderer.Render(Snap([Row(("中文", _plain))]), 10, 2);

        Assert.Contains("\u001b[1;1H中文      ", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void WideCharacterThatDoesNotFitIsLeftOutOfTheLastColumn()
    {
        var renderer = new AnsiScreenRenderer();

        string frame = renderer.Render(Snap([Row(("ab中", _plain))]), 3, 2);

        Assert.Contains("\u001b[1;1Hab \u001b[", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("中", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharactersInCellTextNeverReachTheOutput()
    {
        var renderer = new AnsiScreenRenderer();

        string frame = renderer.Render(Snap([Row(("a\u001b[31mb\u0007c", _plain))]), 10, 2);

        Assert.Contains("a[31mbc", frame, StringComparison.Ordinal);
        Assert.Equal(0, CountOf(frame, "\u0007"));
        Assert.Equal(0, CountOf(frame, "\u001b[31m"));
    }

    [Fact]
    public void OutputIsClampedToTheConsoleSize()
    {
        var renderer = new AnsiScreenRenderer();
        var rows = new[] { Row(("0123456789", _plain)), Row(("second", _plain)), Row(("third", _plain)) };

        string frame = renderer.Render(Snap(rows), 5, 2);

        Assert.Contains("\u001b[1;1H01234", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("second", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("third", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[3;", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertModeHidesTheCursorWhenTheProgramHidesIt()
    {
        var renderer = new AnsiScreenRenderer();
        renderer.Render(Snap([Row(("$ ", _plain))], cursorRow: 0, cursorColumn: 2, mode: "INSERT"), 8, 3);

        string frame = renderer.Render(
            Snap([Row(("$ ", _plain))], cursorRow: 0, cursorColumn: 2, mode: "INSERT", cursorVisible: false),
            8,
            3);

        Assert.Equal(_hideCursor, frame);
    }

    [Fact]
    public void CommandLineReplacesTheStatusAndPutsTheCursorAfterIt()
    {
        var renderer = new AnsiScreenRenderer();
        var snapshot = Snap([Row(("x", _plain))]) with
        {
            ModeName = "COMMAND",
            CommandLine = ":q",
        };

        string frame = renderer.Render(snapshot, 10, 2);

        Assert.Contains("\u001b[2;1H" + _statusSgr + ":q", frame, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[2;3H" + _showCursor, frame, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidatingRedrawsEveryRow()
    {
        var renderer = new AnsiScreenRenderer();
        var snapshot = Snap([Row(("same", _plain))]);
        renderer.Render(snapshot, 6, 2);

        renderer.Invalidate();

        Assert.Contains("\u001b[1;1Hsame  ", renderer.Render(snapshot, 6, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyConsoleProducesNothing()
    {
        Assert.Equal(string.Empty, new AnsiScreenRenderer().Render(Snap([]), 0, 5));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(part, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += part.Length;
        }

        return count;
    }
}
