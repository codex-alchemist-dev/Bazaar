using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Coding.Workspace;
using Bazaar.Server.Host.Coding;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class GuestChannelFileAdapterTests
{
    private sealed class FakeGuest : IGuestChannel
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public (string Path, byte[] Content, string Sha)? Put { get; private set; }

        public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct) =>
            throw new NotImplementedException();

        public async Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            Put = (guestPath, copy.ToArray(), sha256);
        }

        public async Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct)
        {
            if (!Files.TryGetValue(guestPath, out var bytes))
            {
                throw new FileNotFoundException("missing", guestPath);
            }

            await sink.WriteAsync(bytes, ct);
        }
    }

    [Fact]
    public async Task Get_returns_the_bytes_and_null_for_a_missing_file()
    {
        var guest = new FakeGuest();
        guest.Files["/w/a.txt"] = "hi"u8.ToArray();
        var adapter = new GuestChannelFileAdapter(guest);

        Assert.Equal("hi"u8.ToArray(), await adapter.GetFileAsync("/w/a.txt", CancellationToken.None));
        Assert.Null(await adapter.GetFileAsync("/w/none.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Put_streams_the_content_with_its_hash()
    {
        var guest = new FakeGuest();
        var adapter = new GuestChannelFileAdapter(guest);
        var content = Encoding.UTF8.GetBytes("body");
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));

        await adapter.PutFileAsync("/w/b.txt", content, sha, CancellationToken.None);

        Assert.Equal(("/w/b.txt", content, sha), guest.Put);
    }

    [Fact]
    public async Task Delete_list_and_resolve_real_path_fail_closed_until_the_guest_protocol_has_them()
    {
        var adapter = new GuestChannelFileAdapter(new FakeGuest());

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.DeleteFileAsync("/w/a", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ListFilesAsync("/w", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ResolveRealPathAsync("/w/a", CancellationToken.None));
    }
}
