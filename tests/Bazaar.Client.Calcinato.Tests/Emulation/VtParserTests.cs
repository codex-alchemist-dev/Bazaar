using System.Text;
using Bazaar.Client.Calcinato.Emulation;

namespace Bazaar.Client.Calcinato.Tests.Emulation;

public sealed class VtParserTests
{
    [Fact]
    public void PrintsAsciiText()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("hi!"));

        Assert.Equal(["P:h", "P:i", "P:!"], sink.Events);
    }

    [Fact]
    public void CsiSgrWithColourParametersIsDispatched()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[38;5;196m"));

        Assert.Equal(["C:m:38,5,196"], sink.Events);
    }

    [Fact]
    public void CsiWithoutParametersHasOneZeroParameter()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[m"));

        Assert.Equal(["C:m:0"], sink.Events);
    }

    [Fact]
    public void PrivateMarkerAndIntermediateAreReported()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[?1049h\u001b[2 q"));

        Assert.Equal(["C:h?:1049", "C:q :2"], sink.Events);
    }

    [Fact]
    public void SequenceSplitAcrossFeedCallsIsParsed()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[3"));
        parser.Feed(Bytes("1"));
        parser.Feed(Bytes("m"));

        Assert.Equal(["C:m:31"], sink.Events);
    }

    [Fact]
    public void Utf8SplitAcrossFeedCallsDecodesOneCharacter()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);
        byte[] euro = Encoding.UTF8.GetBytes("\u20AC");

        parser.Feed(euro.AsSpan(0, 1));
        parser.Feed(euro.AsSpan(1, 1));
        parser.Feed(euro.AsSpan(2, 1));

        Assert.Equal(["P:\u20AC"], sink.Events);
    }

    [Fact]
    public void Utf4ByteCharacterSplitEverywhereDecodes()
    {
        byte[] emoji = Encoding.UTF8.GetBytes("\U0001F600");
        for (int split = 1; split < emoji.Length; split++)
        {
            var sink = new RecordingSink();
            var parser = new VtParser(sink);
            parser.Feed(emoji.AsSpan(0, split));
            parser.Feed(emoji.AsSpan(split));
            Assert.Equal(["P:\U0001F600"], sink.Events);
        }
    }

    [Fact]
    public void InvalidUtf8BecomesReplacementCharacter()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(new byte[] { 0xFF, 0x41, 0xC3, 0x41 });

        Assert.Equal(["P:\uFFFD", "P:A", "P:\uFFFD", "P:A"], sink.Events);
    }

    [Fact]
    public void OverlongUtf8IsRejected()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(new byte[] { 0xC0, 0xAF });

        Assert.Equal(["P:\uFFFD", "P:\uFFFD"], sink.Events);
    }

    [Fact]
    public void C0ControlsExecuteAndSoSiAreReported()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(new byte[] { 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x07, 0x0E, 0x0F });

        Assert.Equal(["X:08", "X:09", "X:0a", "X:0b", "X:0c", "X:0d", "X:07", "X:0e", "X:0f"], sink.Events);
    }

    [Fact]
    public void ControlInsideCsiExecutesAndSequenceContinues()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[1\nA"));

        Assert.Equal(["X:0a", "C:A:1"], sink.Events);
    }

    [Fact]
    public void CanCancelsCsiWithoutDispatch()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[31\u0018m"));

        Assert.Equal(["P:m"], sink.Events);
    }

    [Fact]
    public void SubCancelsCsiWithoutDispatch()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[31\u001am"));

        Assert.Equal(["P:m"], sink.Events);
    }

    [Fact]
    public void EscapeInsideCsiRestartsAtEscape()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[31\u001b[0m"));

        Assert.Equal(["C:m:0"], sink.Events);
    }

    [Fact]
    public void OscTerminatedByBelIsDispatched()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b]0;title\u0007"));

        Assert.Equal(["O:0;title"], sink.Events);
    }

    [Fact]
    public void OscTerminatedByStIsDispatched()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b]2;two\u001b\\"));

        Assert.Equal(["O:2;two", "E:\\"], sink.Events);
    }

    [Fact]
    public void OscLongerThanCapIsDiscarded()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);
        string body = new('a', VtParser.MaxOscBytes + 1);

        parser.Feed(Bytes("\u001b]0;" + body + "\u0007"));

        Assert.Equal([], sink.Events);
        Assert.True(parser.RejectedSequenceCount > 0);
    }

    [Fact]
    public void OscAtExactlyTheCapIsKept()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);
        string body = new('a', VtParser.MaxOscBytes - 2);

        parser.Feed(Bytes("\u001b]0;" + body + "\u0007"));

        Assert.Single(sink.Events);
        Assert.Equal("O:0;" + body, sink.Events[0]);
    }

    [Fact]
    public void OscAfterOversizedOneIsParsedNormally()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);
        string body = new('b', VtParser.MaxOscBytes + 10);

        parser.Feed(Bytes("\u001b]0;" + body + "\u0007\u001b]0;ok\u0007"));

        Assert.Equal(["O:0;ok"], sink.Events);
    }

    [Fact]
    public void SixelAndDcsAreConsumedAndIgnored()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001bPq#0;2;0;0;0#0!10~-\u001b\\after"));

        Assert.Equal(["E:\\", "P:a", "P:f", "P:t", "P:e", "P:r"], sink.Events);
    }

    [Fact]
    public void SosPmAndApcAreConsumedAndIgnored()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001bXsos\u001b\\\u001b^pm\u001b\\\u001b_apc\u001b\\x"));

        Assert.Equal(["E:\\", "E:\\", "E:\\", "P:x"], sink.Events);
    }

    [Fact]
    public void EscWithIntermediateIsDispatchedWithIntermediate()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b(B\u001b7"));

        Assert.Equal(["E:(B", "E:7"], sink.Events);
    }

    [Fact]
    public void ExcessParametersDiscardTheWholeSequence()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);
        string parameters = string.Join(";", Enumerable.Repeat("1", VtParser.MaxParameters + 1));

        parser.Feed(Bytes("\u001b[" + parameters + "m"));

        Assert.Equal([], sink.Events);
        Assert.Equal(1, parser.RejectedSequenceCount);
    }

    [Fact]
    public void SixteenParametersAreKept()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);
        string parameters = string.Join(";", Enumerable.Range(1, VtParser.MaxParameters));

        parser.Feed(Bytes("\u001b[" + parameters + "m"));

        Assert.Equal(["C:m:" + parameters.Replace(';', ',')], sink.Events);
    }

    [Fact]
    public void HugeParameterValueIsClamped()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[99999999999999m"));

        Assert.Equal(["C:m:65535"], sink.Events);
    }

    [Fact]
    public void TooManyIntermediatesDiscardTheSequence()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[ !\"q"));

        Assert.Equal([], sink.Events);
        Assert.True(parser.RejectedSequenceCount > 0);
    }

    [Fact]
    public void ParameterAfterPrivateMarkerInWrongPlaceIsIgnored()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[1?2m\u001b[3m"));

        Assert.Equal(["C:m:3"], sink.Events);
    }

    [Fact]
    public void ResetDropsPartialSequence()
    {
        var sink = new RecordingSink();
        var parser = new VtParser(sink);

        parser.Feed(Bytes("\u001b[3"));
        parser.Reset();
        parser.Feed(Bytes("m"));

        Assert.Equal(["P:m"], sink.Events);
    }

    [Fact]
    public void ByteByByteFeedMatchesBulkFeed()
    {
        const string text = "\u001b[1;31mred\u001b[0m \u65E5\u672C \u001b]0;t\u0007\u001b[2J";
        byte[] data = Encoding.UTF8.GetBytes(text);

        var bulk = new RecordingSink();
        new VtParser(bulk).Feed(data);

        var single = new RecordingSink();
        var parser = new VtParser(single);
        foreach (byte b in data)
        {
            parser.Feed([b]);
        }

        Assert.Equal(bulk.Events, single.Events);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class RecordingSink : IVtSink
    {
        public List<string> Events { get; } = [];

        public void Print(Rune rune) => Events.Add("P:" + rune.ToString());

        public void Execute(byte control) => Events.Add($"X:{control:x2}");

        public void CsiDispatch(CsiSequence sequence)
        {
            string parameters = string.Join(",", sequence.Parameters.ToArray());
            string prefix = sequence.Prefix == 0 ? string.Empty : ((char)sequence.Prefix).ToString();
            string intermediate = sequence.Intermediate == 0 ? string.Empty : ((char)sequence.Intermediate).ToString();
            Events.Add($"C:{(char)sequence.Final}{prefix}{intermediate}:{parameters}");
        }

        public void EscDispatch(byte intermediate, byte final)
        {
            Events.Add("E:" + (intermediate == 0 ? string.Empty : ((char)intermediate).ToString()) + (char)final);
        }

        public void OscDispatch(ReadOnlySpan<byte> payload) => Events.Add("O:" + Encoding.UTF8.GetString(payload));
    }
}
