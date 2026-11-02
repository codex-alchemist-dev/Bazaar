using System.Security.Cryptography;
using Bazaar.Server.Coding.Workspace;

namespace Bazaar.Server.Tests.Coding.Workspace;

public class InMemoryGuestChannelTests
{
    private const string _guestFile = "/work/agent-1/notes.txt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Put_with_a_wrong_sha256_is_refused_and_nothing_is_stored()
    {
        var channel = new InMemoryGuestChannel();
        var bytes = "hello"u8.ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() => channel.PutFileAsync(_guestFile, new MemoryStream(bytes), new string('0', 64), Ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => channel.GetFileAsync(_guestFile, new MemoryStream(), Ct));
    }

    [Fact]
    public async Task Get_returns_the_bytes_that_were_put()
    {
        var channel = new InMemoryGuestChannel();
        var bytes = "hello"u8.ToArray();
        await channel.PutFileAsync(_guestFile, new MemoryStream(bytes), Sha256Hex(bytes), Ct);

        var sink = new MemoryStream();
        await channel.GetFileAsync(_guestFile, sink, Ct);

        Assert.Equal(bytes, sink.ToArray());
    }
}
