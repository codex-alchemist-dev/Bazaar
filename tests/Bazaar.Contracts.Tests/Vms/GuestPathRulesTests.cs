using System.Text;
using Bazaar.Contracts.Vms;

namespace Bazaar.Contracts.Tests.Vms;

public class GuestPathRulesTests
{
    [Theory]
    [InlineData("a/b.txt", false, false, true)]
    [InlineData("", true, false, true)]
    [InlineData("", false, false, false)]
    [InlineData(".", false, false, false)]
    [InlineData("/etc/passwd", true, false, false)]
    [InlineData("\\x", true, false, false)]
    [InlineData("a/../b", true, false, false)]
    [InlineData("a\\..", true, false, false)]
    [InlineData("C:x", true, false, false)]
    [InlineData("a/b:stream", false, false, false)]
    [InlineData("a/..b/c", false, false, true)]
    [InlineData("a/b.", false, false, true)]
    [InlineData("a/b.", false, true, false)]
    [InlineData("a/b ", false, true, false)]
    [InlineData("a/CON", false, true, false)]
    [InlineData("a/nul.txt", false, true, false)]
    [InlineData("a/com1.log", false, true, false)]
    [InlineData("a/console.txt", false, true, true)]
    [InlineData("a/./b", false, true, true)]
    [InlineData("a/con", false, false, true)]
    public void Safe_relative_paths_follow_the_rules(string path, bool allowRoot, bool windows, bool expected) =>
        Assert.Equal(expected, GuestPathRules.IsSafeRelative(path, allowRoot, windows));

    [Fact]
    public void Paths_over_the_length_limit_and_null_or_nul_paths_are_unsafe()
    {
        Assert.False(GuestPathRules.IsSafeRelative(new string('a', GuestPathRules.MaxPathChars + 1), true));
        Assert.True(GuestPathRules.IsSafeRelative(new string('a', GuestPathRules.MaxPathChars), true));
        Assert.False(GuestPathRules.IsSafeRelative(null, true));
        Assert.False(GuestPathRules.IsSafeRelative("a\0b", true));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData(".git", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\0", false)]
    [InlineData("a:b", false)]
    public void Entry_names_must_be_plain(string name, bool expected) =>
        Assert.Equal(expected, GuestPathRules.IsSafeEntryName(name));

    [Fact]
    public void Every_frame_carries_the_lowest_version_that_defines_its_type()
    {
        Assert.Equal(1, Version(new Exec("e", ["a"], "/", [], 1)));
        Assert.Equal(1, Version(new Heartbeat()));
        Assert.Equal(2, Version(new ExecError("e", "c", "m")));
        Assert.Equal(3, Version(new ListFiles("d")));
        Assert.Equal(3, GuestFrameCodec.Version);
    }

    [Fact]
    public async Task Hello_carries_the_protocol_version_and_an_older_hello_reads_as_zero()
    {
        await using var stream = new MemoryStream();
        await GuestFrameCodec.WriteAsync(stream, new Hello("0.1.0", "vm", "n", 3), TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(3, Assert.IsType<Hello>(await GuestFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken)).ProtocolVersion);

        var body = Encoding.UTF8.GetBytes("{\"agentVersion\":\"0.1.0\",\"vmId\":\"vm\",\"nonce\":\"n\"}");
        var tag = Encoding.ASCII.GetBytes("hello");
        var frame = new byte[4 + 2 + tag.Length + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(frame.Length - 4));
        frame[4] = 1;
        frame[5] = (byte)tag.Length;
        tag.CopyTo(frame, 6);
        body.CopyTo(frame, 6 + tag.Length);

        var old = Assert.IsType<Hello>(await GuestFrameCodec.ReadAsync(new MemoryStream(frame), TestContext.Current.CancellationToken));
        Assert.Equal(0, old.ProtocolVersion);
    }

    private static byte Version(GuestMessage message)
    {
        using var stream = new MemoryStream();
        GuestFrameCodec.WriteAsync(stream, message, CancellationToken.None).GetAwaiter().GetResult();
        return stream.ToArray()[4];
    }
}
