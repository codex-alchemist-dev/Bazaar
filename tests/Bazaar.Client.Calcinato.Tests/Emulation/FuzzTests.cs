using System.Text;
using Bazaar.Client.Calcinato.Emulation;

namespace Bazaar.Client.Calcinato.Tests.Emulation;

public sealed class FuzzTests
{
    private const int _streams = 200;
    private const int _streamLength = 64 * 1024;
    private const int _scrollbackLimit = 1000;

    private static readonly string[] _fragments =
    [
        "\u001b[", "\u001b]", "\u001bP", "\u001b\\", "\u0007", "\u001b7", "\u001b8", "\u001bD", "\u001bM", "\u001bE",
        "\u001b[?1049h", "\u001b[?1049l", "\u001b[?47h", "\u001b[?47l", "\u001b[1;31m", "\u001b[38;5;196m",
        "\u001b[38;2;255;0;0m", "\u001b[48;2;1;2;3m", "\u001b[0m", "\u001b[2J", "\u001b[3J", "\u001b[K", "\u001b[1K",
        "\u001b[5@", "\u001b[3P", "\u001b[2L", "\u001b[2M", "\u001b[3X", "\u001b[2;10r", "\u001b[r", "\u001b[S", "\u001b[T",
        "\u001b[6n", "\u001b[c", "\u001b[5n", "\u001b[?6h", "\u001b[?6l", "\u001b[?7l", "\u001b[?7h", "\u001b[?25l",
        "\u001b[?1000h", "\u001b[?1006h", "\u001b[?2004h", "\u001b]0;title\u0007", "\u001b]8;;https://e.example\u0007",
        "\u001b]8;;\u0007", "\u001b]52;c;QUJD\u0007", "\u001b[18t", "\u001b[1;1;1;1;1;1;1;1;1;1;1;1;1;1;1;1;1;1m",
        "日本", "é", "\U0001F600", "‍", "\r", "\n", "\t", "\b", "\u000E", "\u000F",
    ];

    [Fact]
    public void RandomStreamsNeverThrowAndStayWithinCaps()
    {
        var random = new Random(20261118);

        for (int stream = 0; stream < _streams; stream++)
        {
            var emulator = new Emulator(80, 24, _scrollbackLimit);
            byte[] data = BuildStream(random, _streamLength);

            int offset = 0;
            while (offset < data.Length)
            {
                int chunk = Math.Min(random.Next(1, 2048), data.Length - offset);
                emulator.Feed(data.AsSpan(offset, chunk));
                offset += chunk;

                if (random.Next(64) == 0)
                {
                    emulator.Resize(random.Next(0, 300), random.Next(0, 120));
                }

                AssertCaps(emulator);
            }
        }
    }

    [Fact]
    public void PureRandomBytesNeverThrow()
    {
        var random = new Random(7);

        for (int stream = 0; stream < _streams; stream++)
        {
            var emulator = new Emulator(40, 10, 200);
            var data = new byte[_streamLength];
            random.NextBytes(data);
            emulator.Feed(data);
            AssertCaps(emulator);
        }
    }

    [Fact]
    public void OversizedSequencesStayBoundedUnderLongRuns()
    {
        var emulator = new Emulator(80, 24, 50);
        byte[] longCsi = Encoding.ASCII.GetBytes("\u001b[" + new string('1', 60000) + "m");
        byte[] longOsc = Encoding.ASCII.GetBytes("\u001b]0;" + new string('x', 60000));

        for (int i = 0; i < 20; i++)
        {
            emulator.Feed(longCsi);
            emulator.Feed(longOsc);
            emulator.Feed("\u001b\\\u0007"u8);
        }

        AssertCaps(emulator);
        Assert.True(emulator.RejectedSequenceCount > 0);
    }

    private static byte[] BuildStream(Random random, int length)
    {
        var builder = new StringBuilder(length);
        while (builder.Length < length)
        {
            switch (random.Next(4))
            {
                case 0:
                    builder.Append(_fragments[random.Next(_fragments.Length)]);
                    break;
                case 1:
                    builder.Append((char)random.Next(0x20, 0x7F));
                    break;
                case 2:
                    builder.Append((char)random.Next(0, 0x20));
                    break;
                default:
                    builder.Append(Encoding.UTF8.GetString([(byte)random.Next(256)]));
                    break;
            }
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AssertCaps(Emulator emulator)
    {
        Assert.True(emulator.ScrollbackCount <= _scrollbackLimit);
        Assert.InRange(emulator.CursorRow, 0, emulator.Rows - 1);
        Assert.InRange(emulator.CursorCol, 0, emulator.Cols - 1);
        Assert.True(emulator.Title.Length <= ScreenBuffer.MaxTitleLength);
        Assert.True(emulator.Hyperlinks.Count <= ScreenBuffer.MaxHyperlinks);
    }
}
