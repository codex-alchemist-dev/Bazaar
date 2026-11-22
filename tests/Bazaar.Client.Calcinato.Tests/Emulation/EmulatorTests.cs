using System.Diagnostics;
using System.Text;
using Bazaar.Client.Calcinato.Emulation;

namespace Bazaar.Client.Calcinato.Tests.Emulation;

public sealed class EmulatorTests
{
    private static Emulator Make(int cols, int rows, int scrollback = 100) => new(cols, rows, scrollback);

    private static void Feed(Emulator emulator, string text) => emulator.Feed(Encoding.UTF8.GetBytes(text));

    private static string Visible(Emulator emulator, int row) => emulator.GetLineText(emulator.ScrollbackCount + row);

    private static Cell Visible(Emulator emulator, int row, int col) => emulator.GetCell(emulator.ScrollbackCount + row, col);

    private static List<ReadOnlyMemory<byte>> CaptureResponses(Emulator emulator)
    {
        var responses = new List<ReadOnlyMemory<byte>>();
        emulator.Response += responses.Add;
        return responses;
    }

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.ASCII.GetString(bytes.Span);

    [Fact]
    public void GoldenRedForegroundThenResetStyles()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[31mred\u001b[0m plain");

        Assert.Equal(TermColor.FromIndex(1), Visible(emulator, 0, 0).Style.Foreground);
        Assert.Equal(TermColor.FromIndex(1), Visible(emulator, 0, 2).Style.Foreground);
        Assert.Equal(TermColor.Default, Visible(emulator, 0, 4).Style.Foreground);
        Assert.Equal("red plain", Visible(emulator, 0));
    }

    [Fact]
    public void GoldenIndexed256Colour()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[38;5;196mX\u001b[0m");

        Assert.Equal(TermColor.FromIndex(196), Visible(emulator, 0, 0).Style.Foreground);
        Assert.Equal(TermColorKind.Indexed, Visible(emulator, 0, 0).Style.Foreground.Kind);
    }

    [Fact]
    public void GoldenTruecolour()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[38;2;255;0;0mX\u001b[0m");

        Assert.Equal(TermColor.FromRgb(255, 0, 0), Visible(emulator, 0, 0).Style.Foreground);
        Assert.Equal(TermColorKind.Rgb, Visible(emulator, 0, 0).Style.Foreground.Kind);
    }

    [Fact]
    public void GoldenBackgroundColours()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[41mA\u001b[48;5;21mB\u001b[48;2;1;2;3mC\u001b[49mD");

        Assert.Equal(TermColor.FromIndex(1), Visible(emulator, 0, 0).Style.Background);
        Assert.Equal(TermColor.FromIndex(21), Visible(emulator, 0, 1).Style.Background);
        Assert.Equal(TermColor.FromRgb(1, 2, 3), Visible(emulator, 0, 2).Style.Background);
        Assert.Equal(TermColor.Default, Visible(emulator, 0, 3).Style.Background);
    }

    [Fact]
    public void BrightColoursMapToIndexEightToFifteen()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[91mA\u001b[107mB");

        Assert.Equal(TermColor.FromIndex(9), Visible(emulator, 0, 0).Style.Foreground);
        Assert.Equal(TermColor.FromIndex(15), Visible(emulator, 0, 1).Style.Background);
    }

    [Fact]
    public void AttributesSetAndCleared()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[1;2;3;4;7;8;9mA\u001b[22;23;24;27;28;29mB");

        CellAttributes all = CellAttributes.Bold | CellAttributes.Dim | CellAttributes.Italic | CellAttributes.Underline
            | CellAttributes.Reverse | CellAttributes.Hidden | CellAttributes.Strikethrough;
        Assert.Equal(all, Visible(emulator, 0, 0).Style.Attributes);
        Assert.Equal(CellAttributes.None, Visible(emulator, 0, 1).Style.Attributes);
    }

    [Fact]
    public void SgrResetClearsColoursAndAttributes()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b[1;31;42mA\u001b[mB");

        Assert.Equal(CellStyle.Plain, Visible(emulator, 0, 1).Style);
    }

    [Fact]
    public void Utf8SplitAcrossFeedsStoresOneCell()
    {
        Emulator emulator = Make(10, 2);
        byte[] euro = Encoding.UTF8.GetBytes("a€");

        emulator.Feed(euro.AsSpan(0, 2));
        emulator.Feed(euro.AsSpan(2, 1));
        emulator.Feed(euro.AsSpan(3));

        Assert.Equal("a€", Visible(emulator, 0));
        Assert.Equal("€", Visible(emulator, 0, 1).Text);
        Assert.Equal(2, emulator.CursorCol);
    }

    [Fact]
    public void WideCharactersOccupyTwoCells()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "日本");

        Assert.Equal("日", Visible(emulator, 0, 0).Text);
        Assert.Equal(2, Visible(emulator, 0, 0).Width);
        Assert.Equal(0, Visible(emulator, 0, 1).Width);
        Assert.Equal("本", Visible(emulator, 0, 2).Text);
        Assert.Equal(4, emulator.CursorCol);
        Assert.Equal("日本", Visible(emulator, 0));
    }

    [Fact]
    public void WideCharacterOverwritingItsHalfBlanksTheOther()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "日\u001b[1;1Hx");

        Assert.Equal("x", Visible(emulator, 0, 0).Text);
        Assert.Equal(1, Visible(emulator, 0, 1).Width);
        Assert.Equal(" ", Visible(emulator, 0, 1).Text);
    }

    [Fact]
    public void CombiningMarkAttachesToPreviousCell()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "éx");

        Assert.Equal("é", Visible(emulator, 0, 0).Text);
        Assert.Equal(1, Visible(emulator, 0, 0).Width);
        Assert.Equal("x", Visible(emulator, 0, 1).Text);
        Assert.Equal(2, emulator.CursorCol);
    }

    [Fact]
    public void CombiningMarkAfterWideCharacterAttachesToLeadCell()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "日́");

        Assert.Equal("日́", Visible(emulator, 0, 0).Text);
        Assert.Equal(0, Visible(emulator, 0, 1).Width);
    }

    [Fact]
    public void CombiningMarkWithNothingBeforeIsDropped()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "́a");

        Assert.Equal("a", Visible(emulator, 0));
        Assert.Equal(1, emulator.CursorCol);
    }

    [Fact]
    public void CombiningMarksAreCappedPerCell()
    {
        Emulator emulator = Make(10, 2);
        var text = new StringBuilder("e");
        for (int i = 0; i < 100; i++)
        {
            text.Append('́');
        }

        Feed(emulator, text.ToString());

        Assert.True(Visible(emulator, 0, 0).Text.Length <= ScreenBuffer.MaxClusterLength);
    }

    [Fact]
    public void AutowrapWrapsAtRightMargin()
    {
        Emulator emulator = Make(5, 3);

        Feed(emulator, "abcdefg");

        Assert.Equal("abcde", Visible(emulator, 0));
        Assert.Equal("fg", Visible(emulator, 1));
        Assert.Equal(1, emulator.CursorRow);
        Assert.Equal(2, emulator.CursorCol);
    }

    [Fact]
    public void NoAutowrapOverwritesLastColumn()
    {
        Emulator emulator = Make(5, 3);

        Feed(emulator, "\u001b[?7labcdefg");

        Assert.Equal("abcdg", Visible(emulator, 0));
        Assert.Equal(string.Empty, Visible(emulator, 1));
        Assert.Equal(4, emulator.CursorCol);
        Assert.False(emulator.AutoWrap);
    }

    [Fact]
    public void WideCharacterAtLastColumnWrapsFirst()
    {
        Emulator emulator = Make(5, 3);

        Feed(emulator, "abcd日");

        Assert.Equal("abcd", Visible(emulator, 0));
        Assert.Equal("日", Visible(emulator, 1, 0).Text);
        Assert.Equal(2, Visible(emulator, 1, 0).Width);
    }

    [Fact]
    public void WideCharacterWithoutAutowrapIsDropped()
    {
        Emulator emulator = Make(5, 3);

        Feed(emulator, "\u001b[?7labcd日");

        Assert.Equal("abcd", Visible(emulator, 0));
        Assert.Equal(string.Empty, Visible(emulator, 1));
    }

    [Fact]
    public void CursorMovesClampToScreen()
    {
        Emulator emulator = Make(10, 5);

        Feed(emulator, "\u001b[3;4H");
        Assert.Equal((2, 3), (emulator.CursorRow, emulator.CursorCol));

        Feed(emulator, "\u001b[A");
        Assert.Equal((1, 3), (emulator.CursorRow, emulator.CursorCol));

        Feed(emulator, "\u001b[99B");
        Assert.Equal((4, 3), (emulator.CursorRow, emulator.CursorCol));

        Feed(emulator, "\u001b[2C");
        Assert.Equal(5, emulator.CursorCol);

        Feed(emulator, "\u001b[99D");
        Assert.Equal(0, emulator.CursorCol);

        Feed(emulator, "\u001b[99;99H");
        Assert.Equal((4, 9), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void CursorLineAndColumnMotions()
    {
        Emulator emulator = Make(10, 5);

        Feed(emulator, "\u001b[3;5H\u001b[2E");
        Assert.Equal((4, 0), (emulator.CursorRow, emulator.CursorCol));

        Feed(emulator, "\u001b[2F");
        Assert.Equal((2, 0), (emulator.CursorRow, emulator.CursorCol));

        Feed(emulator, "\u001b[7G");
        Assert.Equal(6, emulator.CursorCol);

        Feed(emulator, "\u001b[5d");
        Assert.Equal(4, emulator.CursorRow);

        Feed(emulator, "\u001b[2;2f");
        Assert.Equal((1, 1), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void SaveAndRestoreCursorWithEscAndCsi()
    {
        Emulator emulator = Make(10, 5);

        Feed(emulator, "\u001b[2;3H\u001b7\u001b[1;1H\u001b8");
        Assert.Equal((1, 2), (emulator.CursorRow, emulator.CursorCol));

        Feed(emulator, "\u001b[4;4H\u001b[s\u001b[1;1H\u001b[u");
        Assert.Equal((3, 3), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void TabMovesToNextStopAndBackspaceMovesLeft()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "a\tb\bc");

        Assert.Equal("a       c", Visible(emulator, 0).Substring(0, 9));
        Assert.Equal("c", Visible(emulator, 0, 8).Text);
    }

    [Fact]
    public void CarriageReturnAndLineFeedAndVerticalTab()
    {
        Emulator emulator = Make(10, 3);

        Feed(emulator, "ab\rX\vY");

        Assert.Equal("Xb", Visible(emulator, 0));
        Assert.Equal(" Y", Visible(emulator, 1));
    }

    [Fact]
    public void EraseDisplayBelowFromCursor()
    {
        Emulator emulator = Make(5, 3);
        Feed(emulator, "11111\r\n22222\r\n33333\u001b[2;3H\u001b[J");

        Assert.Equal("11111", Visible(emulator, 0));
        Assert.Equal("22", Visible(emulator, 1));
        Assert.Equal(string.Empty, Visible(emulator, 2));
    }

    [Fact]
    public void EraseDisplayAboveToCursor()
    {
        Emulator emulator = Make(5, 3);
        Feed(emulator, "11111\r\n22222\r\n33333\u001b[2;3H\u001b[1J");

        Assert.Equal(string.Empty, Visible(emulator, 0));
        Assert.Equal("   22", Visible(emulator, 1));
        Assert.Equal("33333", Visible(emulator, 2));
    }

    [Fact]
    public void EraseDisplayAllClearsVisibleArea()
    {
        Emulator emulator = Make(5, 3);
        Feed(emulator, "11111\r\n22222\u001b[2J");

        for (int row = 0; row < 3; row++)
        {
            Assert.Equal(string.Empty, Visible(emulator, row));
        }
    }

    [Fact]
    public void EraseLineModes()
    {
        Emulator emulator = Make(5, 2);
        Feed(emulator, "abcde\u001b[1;3H\u001b[K");
        Assert.Equal("ab", Visible(emulator, 0));

        Feed(emulator, "\u001b[1;1Habcde\u001b[1;3H\u001b[1K");
        Assert.Equal("   de", Visible(emulator, 0));

        Feed(emulator, "\u001b[2K");
        Assert.Equal(string.Empty, Visible(emulator, 0));
    }

    [Fact]
    public void EraseCharactersLeavesTheRestInPlace()
    {
        Emulator emulator = Make(5, 2);
        Feed(emulator, "abcde\u001b[1;2H\u001b[2X");

        Assert.Equal("a  de", Visible(emulator, 0));
    }

    [Fact]
    public void InsertCharactersShiftsRightAndDropsOverflow()
    {
        Emulator emulator = Make(5, 2);
        Feed(emulator, "abcde\u001b[1;2H\u001b[2@");

        Assert.Equal("a  bc", Visible(emulator, 0));
    }

    [Fact]
    public void DeleteCharactersShiftsLeft()
    {
        Emulator emulator = Make(5, 2);
        Feed(emulator, "abcde\u001b[1;2H\u001b[2P");

        Assert.Equal("ade", Visible(emulator, 0));
    }

    [Fact]
    public void InsertLinesPushesRowsDownWithinScreen()
    {
        Emulator emulator = Make(4, 4);
        Feed(emulator, "1111\r\n2222\r\n3333\r\n4444\u001b[2;1H\u001b[1L");

        Assert.Equal(["1111", string.Empty, "2222", "3333"], Enumerable.Range(0, 4).Select(r => Visible(emulator, r)));
    }

    [Fact]
    public void DeleteLinesPullsRowsUp()
    {
        Emulator emulator = Make(4, 4);
        Feed(emulator, "1111\r\n2222\r\n3333\r\n4444\u001b[2;1H\u001b[1M");

        Assert.Equal(["1111", "3333", "4444", string.Empty], Enumerable.Range(0, 4).Select(r => Visible(emulator, r)));
    }

    [Fact]
    public void ScrollRegionScrollsOnlyInsideRegionAndKeepsScrollback()
    {
        Emulator emulator = Make(4, 5, 10);

        Feed(emulator, "A\r\nB\r\nC\r\nD\r\nE\u001b[2;4r\u001b[4;1H\r\n");

        Assert.Equal(["A", "C", "D", string.Empty, "E"], Enumerable.Range(0, 5).Select(r => Visible(emulator, r)));
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void ScrollUpAndDownInsideRegion()
    {
        Emulator emulator = Make(4, 4, 10);
        Feed(emulator, "1\r\n2\r\n3\r\n4\u001b[2;3r\u001b[1S");

        Assert.Equal(["1", "3", string.Empty, "4"], Enumerable.Range(0, 4).Select(r => Visible(emulator, r)));

        Feed(emulator, "\u001b[1T");
        Assert.Equal(["1", string.Empty, "3", "4"], Enumerable.Range(0, 4).Select(r => Visible(emulator, r)));
    }

    [Fact]
    public void ReverseIndexAtTopOfRegionScrollsDown()
    {
        Emulator emulator = Make(4, 3, 10);
        Feed(emulator, "a\r\nb\u001b[1;1H\u001bM");

        Assert.Equal(["", "a", "b"], Enumerable.Range(0, 3).Select(r => Visible(emulator, r)));
        Assert.Equal(0, emulator.CursorRow);
    }

    [Fact]
    public void IndexAndNextLine()
    {
        Emulator emulator = Make(4, 3, 10);

        Feed(emulator, "a\u001bDb\u001bEc");

        Assert.Equal(["a", " b", "c"], Enumerable.Range(0, 3).Select(r => Visible(emulator, r)));
    }

    [Fact]
    public void LineFeedAtBottomPushesTopLineIntoScrollback()
    {
        Emulator emulator = Make(4, 2, 10);

        Feed(emulator, "a\r\nb\r\nc");

        Assert.Equal(1, emulator.ScrollbackCount);
        Assert.Equal("a", emulator.GetLineText(0));
        Assert.Equal("b", Visible(emulator, 0));
        Assert.Equal("c", Visible(emulator, 1));
    }

    [Fact]
    public void ScrollbackNeverExceedsLimit()
    {
        Emulator emulator = Make(10, 3, 5);
        var text = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            text.Append('L').Append(i).Append(i < 99 ? "\r\n" : string.Empty);
        }

        Feed(emulator, text.ToString());

        Assert.Equal(5, emulator.ScrollbackCount);
        Assert.Equal("L92", emulator.GetLineText(0));
        Assert.Equal("L99", Visible(emulator, 2));
    }

    [Fact]
    public void ZeroScrollbackKeepsNothing()
    {
        Emulator emulator = Make(4, 2, 0);

        Feed(emulator, "a\r\nb\r\nc");

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal("b", Visible(emulator, 0));
    }

    [Fact]
    public void AlternateScreenSavesAndRestoresPrimary()
    {
        Emulator emulator = Make(10, 3, 10);
        Feed(emulator, "primary\u001b[1;4H");

        Feed(emulator, "\u001b[?1049h");
        Assert.True(emulator.IsAlternateScreen);
        Assert.Equal(string.Empty, Visible(emulator, 0));
        Feed(emulator, "alt");
        Assert.Equal("alt", Visible(emulator, 0));

        Feed(emulator, "\u001b[?1049l");
        Assert.False(emulator.IsAlternateScreen);
        Assert.Equal("primary", Visible(emulator, 0));
        Assert.Equal((0, 3), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void AlternateScreenHasNoScrollback()
    {
        Emulator emulator = Make(3, 2, 10);
        Feed(emulator, "a\r\nb\r\nc");
        int primaryScrollback = emulator.ScrollbackCount;

        Feed(emulator, "\u001b[?1049h1\r\n2\r\n3\r\n4");

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.True(primaryScrollback > 0);
    }

    [Fact]
    public void Mode47SwitchesWithoutClearing()
    {
        Emulator emulator = Make(10, 2, 10);
        Feed(emulator, "\u001b[?47h");
        Feed(emulator, "kept");
        Feed(emulator, "\u001b[?47l\u001b[?47h");

        Assert.True(emulator.IsAlternateScreen);
        Assert.Equal("kept", Visible(emulator, 0));
    }

    [Fact]
    public void ResizeReflowsWrappedLinesOnPrimary()
    {
        Emulator emulator = Make(10, 5);
        Feed(emulator, "abcdefghijkl");
        Assert.Equal("abcdefghij", Visible(emulator, 0));
        Assert.Equal("kl", Visible(emulator, 1));

        emulator.Resize(5, 5);
        Assert.Equal("abcde", Visible(emulator, 0));
        Assert.Equal("fghij", Visible(emulator, 1));
        Assert.Equal("kl", Visible(emulator, 2));
        Assert.Equal((2, 2), (emulator.CursorRow, emulator.CursorCol));

        emulator.Resize(20, 5);
        Assert.Equal("abcdefghijkl", Visible(emulator, 0));
        Assert.Equal((0, 12), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void ResizeReflowKeepsWideCharactersWhole()
    {
        Emulator emulator = Make(6, 4);
        Feed(emulator, "ab日本c");

        emulator.Resize(3, 4);

        Assert.Equal("ab", Visible(emulator, 0));
        Assert.Equal("日", Visible(emulator, 1));
        Assert.Equal("本c", Visible(emulator, 2));
        Assert.Equal(2, Visible(emulator, 1, 0).Width);
        Assert.Equal(0, Visible(emulator, 1, 1).Width);
        Assert.Equal(2, Visible(emulator, 2, 0).Width);
    }

    [Fact]
    public void ResizeMovesScrollbackIntoVisibleOnGrow()
    {
        Emulator emulator = Make(4, 2, 10);
        Feed(emulator, "a\r\nb\r\nc");
        Assert.Equal(1, emulator.ScrollbackCount);

        emulator.Resize(4, 4);

        Assert.Equal(["a", "b", "c", string.Empty], Enumerable.Range(0, 4).Select(r => emulator.GetLineText(r)));
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void ResizeAlternateScreenKeepsContentWithoutReflow()
    {
        Emulator emulator = Make(10, 3);
        Feed(emulator, "\u001b[?1049hhello world");

        emulator.Resize(6, 3);

        Assert.Equal("hello", Visible(emulator, 0));
        Assert.Equal(6, emulator.Cols);
        Assert.True(emulator.IsAlternateScreen);
    }

    [Fact]
    public void ResizeClampsCursor()
    {
        Emulator emulator = Make(10, 5);
        Feed(emulator, "\u001b[5;9H");

        emulator.Resize(4, 2);

        Assert.True(emulator.CursorRow < 2);
        Assert.True(emulator.CursorCol < 4);
    }

    [Fact]
    public void ResizeToZeroIsClampedToOneCell()
    {
        Emulator emulator = Make(10, 5);

        emulator.Resize(0, -3);

        Assert.Equal(1, emulator.Cols);
        Assert.Equal(1, emulator.Rows);
    }

    [Fact]
    public void TitleFromOsc0AndOsc2()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "\u001b]0;My Title\u0007");
        Assert.Equal("My Title", emulator.Title);

        Feed(emulator, "\u001b]2;Second\u001b\\");
        Assert.Equal("Second", emulator.Title);
    }

    [Fact]
    public void TitleStripsControlCharactersAndSeparators()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "\u001b]2;a\u0001b\u007Fc\u009Bd\u2028e\u0007");

        Assert.Equal("abcde", emulator.Title);
    }

    [Fact]
    public void TitleIsCappedAt256Characters()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "\u001b]0;" + new string('x', 300) + "\u0007");

        Assert.Equal(256, emulator.Title.Length);
    }

    [Fact]
    public void TitleReportQueryIsRefused()
    {
        Emulator emulator = Make(10, 2);
        Feed(emulator, "\u001b]0;keep\u0007");

        Feed(emulator, "\u001b]0;?\u0007");

        Assert.Equal("keep", emulator.Title);
    }

    [Fact]
    public void Osc52ClipboardWriteIsIgnoredAndSendsNothing()
    {
        Emulator emulator = Make(10, 2);
        Feed(emulator, "\u001b]0;keep\u0007");
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001b]52;c;SGVsbG8=\u0007");

        Assert.Equal("keep", emulator.Title);
        Assert.Empty(responses);
        Assert.Equal(string.Empty, Visible(emulator, 0));
    }

    [Fact]
    public void OversizedTitleOscIsDropped()
    {
        Emulator emulator = Make(10, 2);
        Feed(emulator, "\u001b]0;keep\u0007");

        Feed(emulator, "\u001b]0;" + new string('z', 5000) + "\u0007");

        Assert.Equal("keep", emulator.Title);
    }

    [Fact]
    public void HyperlinksAreStoredWithSpanAndNotOpened()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b]8;;https://example.com/a\u001b\\link\u001b]8;;\u001b\\ plain");

        Assert.Single(emulator.Hyperlinks);
        (string uri, int row, int startCol, int endCol) = emulator.Hyperlinks[0];
        Assert.Equal("https://example.com/a", uri);
        Assert.Equal((0, 0, 3), (row, startCol, endCol));
    }

    [Fact]
    public void HyperlinkRowIsAbsoluteAfterScrolling()
    {
        Emulator emulator = Make(20, 2, 10);

        Feed(emulator, "\u001b]8;;https://example.com\u0007x\u001b]8;;\u0007\r\ny\r\nz");

        Assert.Single(emulator.Hyperlinks);
        Assert.Equal(0, emulator.Hyperlinks[0].Row);
        Assert.Equal(1, emulator.ScrollbackCount);
    }

    [Fact]
    public void HyperlinkWithEmptyUriDoesNotOpen()
    {
        Emulator emulator = Make(20, 2);

        Feed(emulator, "\u001b]8;;\u0007abc");

        Assert.Empty(emulator.Hyperlinks);
    }

    [Fact]
    public void DeviceAttributesReplyIsSent()
    {
        Emulator emulator = Make(10, 2);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001b[c\u001b[0c");

        Assert.Equal(2, responses.Count);
        Assert.Equal("\u001b[?62;22c", Text(responses[0]));
    }

    [Fact]
    public void CursorPositionReportIsOneBased()
    {
        Emulator emulator = Make(10, 5);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001b[3;4H\u001b[6n");

        Assert.Equal("\u001b[3;4R", Text(Assert.Single(responses)));
    }

    [Fact]
    public void CursorPositionReportIsOriginRelativeInOriginMode()
    {
        Emulator emulator = Make(10, 6);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001b[3;5r\u001b[?6h\u001b[2;2H\u001b[6n");

        Assert.Equal("\u001b[2;2R", Text(Assert.Single(responses)));
    }

    [Fact]
    public void OperatingStatusReportIsSent()
    {
        Emulator emulator = Make(10, 5);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001b[5n");

        Assert.Equal("\u001b[0n", Text(Assert.Single(responses)));
    }

    [Fact]
    public void ResponsesAreRateLimited()
    {
        var clock = new ManualClock();
        var emulator = new Emulator(10, 2, 10, clock);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);
        string burst = string.Concat(Enumerable.Repeat("\u001b[c", 100));

        Feed(emulator, burst);

        Assert.Equal(16, responses.Count);
        Assert.Equal(84, emulator.ResponsesDropped);

        clock.Advance(TimeSpan.FromSeconds(1));
        Feed(emulator, burst);

        Assert.Equal(32, responses.Count);
        Assert.Equal(168, emulator.ResponsesDropped);
    }

    [Fact]
    public void RateLimitRefillsSlowlyOverTime()
    {
        var clock = new ManualClock();
        var emulator = new Emulator(10, 2, 10, clock);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);
        Feed(emulator, string.Concat(Enumerable.Repeat("\u001b[c", 16)));
        Assert.Equal(16, responses.Count);

        clock.Advance(TimeSpan.FromMilliseconds(125));
        Feed(emulator, "\u001b[c\u001b[c");

        Assert.Equal(18, responses.Count);
    }

    [Fact]
    public void UnknownDeviceQueriesAreIgnored()
    {
        Emulator emulator = Make(10, 5);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001b[18t\u001b[>c\u001b[?6n\u001b[1;2;3;4;5;6$");

        Assert.Empty(responses);
        Assert.Equal(string.Empty, Visible(emulator, 0));
    }

    [Fact]
    public void SixelAndDcsSendNothingAndDrawNothing()
    {
        Emulator emulator = Make(20, 2);
        List<ReadOnlyMemory<byte>> responses = CaptureResponses(emulator);

        Feed(emulator, "\u001bP0;0;0q\"1;1;100;100#0;2;0;0;0#0!100~\u001b\\hi");

        Assert.Empty(responses);
        Assert.Equal("hi", Visible(emulator, 0));
    }

    [Fact]
    public void ModeFlagsAreRecorded()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "\u001b[?1h\u001b[?2004h\u001b[?25l\u001b[?1000h\u001b[?1002h\u001b[?1006h");

        Assert.True(emulator.ApplicationCursorKeys);
        Assert.True(emulator.BracketedPaste);
        Assert.False(emulator.CursorVisible);
        Assert.Equal(MouseModes.Press | MouseModes.Drag | MouseModes.Sgr, emulator.MouseMode);

        Feed(emulator, "\u001b[?1000l\u001b[?1l\u001b[?25h");
        Assert.Equal(MouseModes.Drag | MouseModes.Sgr, emulator.MouseMode);
        Assert.False(emulator.ApplicationCursorKeys);
        Assert.True(emulator.CursorVisible);
    }

    [Fact]
    public void ModesDefaultToXtermValues()
    {
        Emulator emulator = Make(10, 2);

        Assert.True(emulator.AutoWrap);
        Assert.True(emulator.CursorVisible);
        Assert.False(emulator.OriginMode);
        Assert.False(emulator.ApplicationCursorKeys);
        Assert.False(emulator.BracketedPaste);
        Assert.Equal(MouseModes.None, emulator.MouseMode);
    }

    [Fact]
    public void OriginModeAddressesInsideScrollRegion()
    {
        Emulator emulator = Make(5, 6);

        Feed(emulator, "\u001b[2;4r\u001b[?6h");
        Assert.Equal(1, emulator.CursorRow);

        Feed(emulator, "\u001b[1;1H");
        Assert.Equal(1, emulator.CursorRow);

        Feed(emulator, "\u001b[9;1H");
        Assert.Equal(3, emulator.CursorRow);

        Feed(emulator, "\u001b[?6l\u001b[1;1H");
        Assert.Equal(0, emulator.CursorRow);
    }

    [Fact]
    public void InvalidScrollRegionIsIgnored()
    {
        Emulator emulator = Make(5, 4);

        Feed(emulator, "\u001b[3;3r\u001b[9;2r");

        Feed(emulator, "a\r\nb\r\nc\r\nd\r\ne");
        Assert.Equal(1, emulator.ScrollbackCount);
    }

    [Fact]
    public void BellAndShiftOutAndInAreSilent()
    {
        Emulator emulator = Make(10, 2);

        Feed(emulator, "a\u0007\u000Eb\u000Fc");

        Assert.Equal("abc", Visible(emulator, 0));
    }

    [Fact]
    public void GetCellOutOfRangeReturnsBlank()
    {
        Emulator emulator = Make(4, 2);

        Assert.Equal(Cell.Blank(CellStyle.Plain).Text, emulator.GetCell(-1, 0).Text);
        Assert.Equal(1, emulator.GetCell(99, 0).Width);
        Assert.Equal(1, emulator.GetCell(0, 99).Width);
        Assert.Equal(string.Empty, emulator.GetLineText(99));
    }

    [Fact]
    public void EmptyFeedChangesNothing()
    {
        Emulator emulator = Make(4, 2);

        emulator.Feed(ReadOnlySpan<byte>.Empty);

        Assert.Equal((0, 0), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void GoldenMixedStreamMatchesExpectedCells()
    {
        Emulator emulator = Make(40, 3);

        Feed(emulator, "\u001b[31mred\u001b[0m \u001b[38;5;196mX\u001b[0m \u001b[38;2;255;0;0mY\u001b[0m");

        Assert.Equal("red X Y", Visible(emulator, 0));
        Assert.Equal(TermColor.FromIndex(1), Visible(emulator, 0, 0).Style.Foreground);
        Assert.Equal(TermColor.FromIndex(196), Visible(emulator, 0, 4).Style.Foreground);
        Assert.Equal(TermColor.FromRgb(255, 0, 0), Visible(emulator, 0, 6).Style.Foreground);
        Assert.Equal(CellStyle.Plain, Visible(emulator, 0, 5).Style);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan span) => _timestamp += (long)(span.TotalSeconds * Stopwatch.Frequency);
    }
}
