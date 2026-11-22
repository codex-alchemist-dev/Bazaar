using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class PathJailTests
{
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData("src\\..\\..\\secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("C:/Windows/system.ini")]
    [InlineData("C:file.txt")]
    [InlineData("D:\\x.txt")]
    [InlineData("src/file.txt:stream")]
    [InlineData("nul\0.txt")]
    [InlineData("CON")]
    [InlineData("src/NUL.txt")]
    [InlineData("com1.log")]
    [InlineData("trailing-dot.")]
    [InlineData("trailing-space ")]
    [InlineData("bad<name>.txt")]
    public void LexicalRefusesEscapesAndDeviceMarkers(string path)
    {
        var decision = PathJail.Lexical(path);

        Assert.False(decision.Allowed);
        Assert.Equal(EditRejectReason.PathEscape, decision.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LexicalRefusesMissingPathAsMalformed(string? path)
    {
        Assert.Equal(EditRejectReason.Malformed, PathJail.Lexical(path).Reason);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void LexicalRefusesTheRootAsMalformed(string path)
    {
        Assert.Equal(EditRejectReason.Malformed, PathJail.Lexical(path).Reason);
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("./src//lib/./b.cs", "src/lib/b.cs")]
    [InlineData("src\\lib\\c.cs", "src/lib/c.cs")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    public void LexicalNormalizesPlainRelativePaths(string path, string expected)
    {
        var decision = PathJail.Lexical(path);

        Assert.True(decision.Allowed);
        Assert.Equal(expected, decision.Path);
    }

    [Fact]
    public async Task AuthorizeRefusesASymlinkThatLeavesTheWorktree()
    {
        var files = new InMemoryWorktreeFiles();
        files.CreateSymlink("link", "../outside");

        var decision = await PathJail.AuthorizeAsync(files, "link/file.txt", CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(EditRejectReason.PathEscape, decision.Reason);
    }

    [Fact]
    public async Task AuthorizeRefusesASymlinkToAnAbsoluteTarget()
    {
        var files = new InMemoryWorktreeFiles();
        files.CreateSymlink("passwd", "/etc/passwd");

        var decision = await PathJail.AuthorizeAsync(files, "passwd", CancellationToken.None);

        Assert.Equal(EditRejectReason.PathEscape, decision.Reason);
    }

    [Fact]
    public async Task AuthorizeRefusesALinkChainThatNeverResolves()
    {
        var files = new InMemoryWorktreeFiles();
        files.CreateSymlink("a", "b");
        files.CreateSymlink("b", "a");

        var decision = await PathJail.AuthorizeAsync(files, "a", CancellationToken.None);

        Assert.Equal(EditRejectReason.PathEscape, decision.Reason);
    }

    [Fact]
    public async Task AuthorizeAllowsASymlinkThatStaysInsideTheWorktree()
    {
        var files = new InMemoryWorktreeFiles();
        files.SetText("src/real.txt", "x");
        files.CreateSymlink("alias.txt", "src/real.txt");

        var decision = await PathJail.AuthorizeAsync(files, "alias.txt", CancellationToken.None);

        Assert.True(decision.Allowed);
        Assert.Equal("alias.txt", decision.Path);
    }

    [Fact]
    public async Task AuthorizeRefusesAStoreThatCannotResolveThePath()
    {
        var files = new UnresolvableWorktreeFiles();

        var decision = await PathJail.AuthorizeAsync(files, "file.txt", CancellationToken.None);

        Assert.Equal(EditRejectReason.PathEscape, decision.Reason);
    }

    /// <summary>A store whose resolution always fails, to check that the jail fails closed.</summary>
    private sealed class UnresolvableWorktreeFiles : IWorktreeFiles
    {
        public Task<WorktreeFile?> ReadAsync(string relativePath, CancellationToken ct) => Task.FromResult<WorktreeFile?>(null);

        public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task WriteAsync(string relativePath, byte[] content, string sha256, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string relativePath, CancellationToken ct) => Task.FromResult(false);

        public Task DeleteAsync(string relativePath, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
