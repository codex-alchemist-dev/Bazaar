using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class InMemoryWorktreeFilesTests
{
    [Fact]
    public async Task ResolvesARelativeLinkFromItsOwnDirectory()
    {
        var files = new InMemoryWorktreeFiles();
        files.SetText("src/lib/real.txt", "data");
        files.CreateSymlink("src/alias", "lib");

        Assert.Equal("src/lib/real.txt", await files.ResolveRealPathAsync("src/alias/real.txt", CancellationToken.None));
        Assert.Equal("data", files.GetText("src/alias/real.txt"));
    }

    [Fact]
    public async Task ResolvesParentSegmentsInsideTheRoot()
    {
        var files = new InMemoryWorktreeFiles();
        files.SetText("a/target.txt", "t");

        Assert.Equal("a/target.txt", await files.ResolveRealPathAsync("b/../a/target.txt", CancellationToken.None));
    }

    [Fact]
    public async Task ClimbingAboveTheRootFailsResolution()
    {
        var files = new InMemoryWorktreeFiles();

        Assert.Null(await files.ResolveRealPathAsync("../up.txt", CancellationToken.None));
    }

    [Fact]
    public async Task ALinkWithAnAbsoluteTargetFailsResolution()
    {
        var files = new InMemoryWorktreeFiles();
        files.CreateSymlink("x", "C:/Windows");

        Assert.Null(await files.ResolveRealPathAsync("x/system.ini", CancellationToken.None));
        Assert.Null(await files.ReadAsync("x/system.ini", CancellationToken.None));
    }

    [Fact]
    public async Task ListsFilesRecursivelyInSortedOrder()
    {
        var files = new InMemoryWorktreeFiles();
        files.SetText("z.txt", "");
        files.SetText("src/b.cs", "");
        files.SetText("src/deep/a.cs", "");
        files.SetText("srcx/other.cs", "");

        Assert.Equal(["src/b.cs", "src/deep/a.cs"], await files.ListAsync("src", CancellationToken.None));
        Assert.Equal(["src/b.cs", "src/deep/a.cs", "srcx/other.cs", "z.txt"], await files.ListAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task WriteRejectsContentThatDoesNotMatchItsHash()
    {
        var files = new InMemoryWorktreeFiles();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            files.WriteAsync("a.txt", [1, 2, 3], new string('0', 64), CancellationToken.None));
        Assert.Equal(0, files.WriteCount);
    }

    [Fact]
    public async Task DeleteRemovesAFileAndIgnoresAMissingOne()
    {
        var files = new InMemoryWorktreeFiles();
        files.SetText("a.txt", "x");

        await files.DeleteAsync("a.txt", CancellationToken.None);
        await files.DeleteAsync("a.txt", CancellationToken.None);

        Assert.False(await files.ExistsAsync("a.txt", CancellationToken.None));
    }
}
