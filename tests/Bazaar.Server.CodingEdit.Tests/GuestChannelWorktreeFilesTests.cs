using System.Text;
using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class GuestChannelWorktreeFilesTests
{
    private const string _root = "/work/agent/wt";

    private readonly InMemoryGuestFileChannel _guest = new();
    private readonly GuestChannelWorktreeFiles _files;

    public GuestChannelWorktreeFilesTests()
    {
        _files = new GuestChannelWorktreeFiles(_guest, _root);
    }

    [Fact]
    public async Task ReadsAFileUnderTheRootWithItsHash()
    {
        _guest.Set("/work/agent/wt/src/a.cs", Encoding.UTF8.GetBytes("class A {}"));

        var file = await _files.ReadAsync("src/a.cs", CancellationToken.None);

        Assert.NotNull(file);
        Assert.Equal("class A {}", Encoding.UTF8.GetString(file.Content));
        Assert.Equal(ContentHash.Sha256Hex(file.Content), file.Sha256);
    }

    [Fact]
    public async Task ReturnsNullForAMissingFile()
    {
        Assert.Null(await _files.ReadAsync("nope.cs", CancellationToken.None));
        Assert.False(await _files.ExistsAsync("nope.cs", CancellationToken.None));
    }

    [Fact]
    public async Task WritesThroughTheGuestWithTheHash()
    {
        var content = Encoding.UTF8.GetBytes("new");

        await _files.WriteAsync("out/new.txt", content, ContentHash.Sha256Hex(content), CancellationToken.None);

        Assert.Equal("new", Encoding.UTF8.GetString(_guest.Get("/work/agent/wt/out/new.txt")!));
        Assert.Equal(1, _guest.PutCount);
    }

    [Fact]
    public async Task RefusesAWriteWhoseHashDoesNotMatch()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.WriteAsync("a.txt", Encoding.UTF8.GetBytes("x"), new string('0', 64), CancellationToken.None));
        Assert.Equal(0, _guest.PutCount);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/x.txt")]
    public async Task RefusesPathsThatSkippedTheJail(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _files.ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task MapsAGuestPathOutsideTheRootToNull()
    {
        _guest.RealPath = _ => "/etc/passwd";

        Assert.Null(await _files.ResolveRealPathAsync("link/x", CancellationToken.None));
    }

    [Fact]
    public async Task MapsAResolvedGuestPathBackToARelativePath()
    {
        _guest.RealPath = _ => "/work/agent/wt/src/real.cs";

        Assert.Equal("src/real.cs", await _files.ResolveRealPathAsync("alias.cs", CancellationToken.None));
    }

    [Fact]
    public async Task ListsOnlyFilesUnderTheRootAsRelativePaths()
    {
        _guest.Set("/work/agent/wt/src/b.cs", []);
        _guest.Set("/work/agent/wt/src/a.cs", []);
        _guest.Set("/work/other/c.cs", []);

        var listed = await _files.ListAsync("src", CancellationToken.None);

        Assert.Equal(["src/a.cs", "src/b.cs"], listed);
    }

    [Fact]
    public async Task DeletesThroughTheGuest()
    {
        _guest.Set("/work/agent/wt/gone.txt", []);

        await _files.DeleteAsync("gone.txt", CancellationToken.None);

        Assert.Null(_guest.Get("/work/agent/wt/gone.txt"));
    }

    [Fact]
    public void RequiresAnAbsoluteRoot()
    {
        Assert.Throws<ArgumentException>(() => new GuestChannelWorktreeFiles(_guest, "relative/root"));
    }

    [Fact]
    public async Task WholeFileEditRunsOverTheGuestChannel()
    {
        _guest.Set("/work/agent/wt/a.txt", Encoding.UTF8.GetBytes("old\n"));
        var tool = new WholeFileEditTool(_files);

        var result = await tool.ApplyAsync(new WholeFileEditRequest("a.txt", "new\n"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("new\n", Encoding.UTF8.GetString(_guest.Get("/work/agent/wt/a.txt")!));
    }
}
