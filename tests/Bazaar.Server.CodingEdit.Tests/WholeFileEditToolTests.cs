using System.Text;
using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class WholeFileEditToolTests
{
    private readonly InMemoryWorktreeFiles _files = new();
    private readonly WholeFileEditTool _tool;

    public WholeFileEditToolTests()
    {
        _tool = new WholeFileEditTool(_files);
    }

    [Fact]
    public async Task CreatesAMissingFileAndReportsTheHashes()
    {
        var result = await _tool.ApplyAsync(new WholeFileEditRequest("docs/new.md", "one\ntwo\n"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Null(result.BeforeSha256);
        Assert.Equal(ContentHash.Sha256Hex(Encoding.UTF8.GetBytes("one\ntwo\n")), result.AfterSha256);
        Assert.Equal(2, result.ChangedLines);
        Assert.Equal("one\ntwo\n", _files.GetText("docs/new.md"));
    }

    [Fact]
    public async Task OverwritesAnExistingFile()
    {
        _files.SetText("a.txt", "old\nkeep\n");

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", "new\nkeep\n"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(1, result.ChangedLines);
        Assert.Equal("new\nkeep\n", _files.GetText("a.txt"));
        Assert.Equal(1, _files.WriteCount);
    }

    [Fact]
    public async Task IsIdempotentWhenTheContentAlreadyHolds()
    {
        _files.SetText("a.txt", "same\n");

        var first = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", "same\n"), CancellationToken.None);
        var second = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", "same\n"), CancellationToken.None);

        Assert.True(first.Applied);
        Assert.True(second.Applied);
        Assert.Equal(0, second.ChangedLines);
        Assert.Equal(0, _files.WriteCount);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/hosts")]
    [InlineData("C:/x.txt")]
    public async Task RefusesEscapingPathsAndWritesNothing(string path)
    {
        var result = await _tool.ApplyAsync(new WholeFileEditRequest(path, "x"), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(EditRejectReason.PathEscape, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesASymlinkThatLeavesTheWorktree()
    {
        _files.CreateSymlink("out", "../outside");

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("out/x.txt", "x"), CancellationToken.None);

        Assert.Equal(EditRejectReason.PathEscape, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task FollowsASymlinkThatStaysInside()
    {
        _files.SetText("src/real.txt", "old\n");
        _files.CreateSymlink("alias.txt", "src/real.txt");

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("alias.txt", "new\n"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("new\n", _files.GetText("src/real.txt"));
    }

    [Fact]
    public async Task RefusesNullContentAsMalformed()
    {
        var result = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", null!), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesContentWithAnUnpairedSurrogate()
    {
        var result = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", "bad\uD800"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesAFileThatIsNotUtf8()
    {
        _files.SetBytes("bin.dat", [0xFF, 0xFE, 0x00]);

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("bin.dat", "text"), CancellationToken.None);

        Assert.Equal(EditRejectReason.NotText, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesAnotherFormatsRequestAsMalformed()
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("a.txt", "x", "y"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesWhenTheFileChangedAfterItWasRead()
    {
        _files.SetText("a.txt", "original\n");
        var changedOnce = false;
        _files.AfterReadHook = path =>
        {
            if (!changedOnce)
            {
                changedOnce = true;
                _files.SetText(path, "concurrent\n");
            }
        };

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", "agent\n"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Conflict, result.Reason);
        Assert.Equal("concurrent\n", _files.GetText("a.txt"));
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RestoresTheOriginalWhenTheWriteDoesNotVerify()
    {
        _files.SetText("a.txt", "original\n");
        // Only the agent's bytes are corrupted, so the restore of the original is stored intact.
        var agentBytes = Encoding.UTF8.GetBytes("agent\n");
        _files.WriteFilter = (_, bytes) => bytes.SequenceEqual(agentBytes) ? Encoding.UTF8.GetBytes("corrupted\n") : bytes;

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("a.txt", "agent\n"), CancellationToken.None);

        Assert.Equal(EditRejectReason.WriteFailed, result.Reason);
        Assert.Equal("original\n", _files.GetText("a.txt"));
    }

    [Fact]
    public async Task RemovesAFileItCreatedWhenItsWriteDoesNotVerify()
    {
        _files.WriteFilter = (_, _) => Encoding.UTF8.GetBytes("corrupted\n");

        var result = await _tool.ApplyAsync(new WholeFileEditRequest("new.txt", "agent\n"), CancellationToken.None);

        Assert.Equal(EditRejectReason.WriteFailed, result.Reason);
        Assert.False(await _files.ExistsAsync("new.txt", CancellationToken.None));
    }
}
