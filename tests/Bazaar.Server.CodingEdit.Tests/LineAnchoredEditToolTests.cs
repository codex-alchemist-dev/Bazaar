using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class LineAnchoredEditToolTests
{
    private const string _source = "alpha\nbeta\ngamma\ndelta\n";

    private readonly InMemoryWorktreeFiles _files = new();
    private readonly LineAnchoredEditTool _tool;

    public LineAnchoredEditToolTests()
    {
        _files.SetText("notes.txt", _source);
        _tool = new LineAnchoredEditTool(_files);
    }

    /// <summary>The hash the model would send for lines in the range, joined with LF and no trailing newline.</summary>
    private static string Anchor(params string[] lines) => ContentHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)));

    [Fact]
    public async Task ReplacesAProvenLineRange()
    {
        var request = new LineAnchoredEditRequest("notes.txt", 2, 3, Anchor("beta", "gamma"), "BETA\nGAMMA\nEPSILON");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("alpha\nBETA\nGAMMA\nEPSILON\ndelta\n", _files.GetText("notes.txt"));
        Assert.Equal(3, result.ChangedLines);
        Assert.Equal(ContentHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(_files.GetText("notes.txt")!)), result.AfterSha256);
    }

    [Fact]
    public async Task DeletesLinesWithAnEmptyReplacement()
    {
        var request = new LineAnchoredEditRequest("notes.txt", 1, 1, Anchor("alpha"), "");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("beta\ngamma\ndelta\n", _files.GetText("notes.txt"));
    }

    [Fact]
    public async Task RefusesAnAnchorMismatchAndWritesNothing()
    {
        var request = new LineAnchoredEditRequest("notes.txt", 2, 2, Anchor("stale"), "BETA");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(EditRejectReason.AnchorMismatch, result.Reason);
        Assert.Equal(_source, _files.GetText("notes.txt"));
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task IsIdempotentWhenTheRangeAlreadyHoldsTheReplacement()
    {
        var first = new LineAnchoredEditRequest("notes.txt", 2, 2, Anchor("beta"), "BETA");
        await _tool.ApplyAsync(first, CancellationToken.None);

        // The anchor is now stale, but the range already holds the replacement, so the repeat is a no-op.
        var second = await _tool.ApplyAsync(first, CancellationToken.None);

        Assert.True(second.Applied);
        Assert.Equal(0, second.ChangedLines);
        Assert.Equal("alpha\nBETA\ngamma\ndelta\n", _files.GetText("notes.txt"));
        Assert.Equal(1, _files.WriteCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 9)]
    public async Task RefusesAnInvalidOrOutOfRangeLineRangeAsMalformed(int start, int end)
    {
        var request = new LineAnchoredEditRequest("notes.txt", start, end, Anchor("alpha"), "x");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(_source, _files.GetText("notes.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    public async Task RefusesAMalformedHashAsMalformed(string hash)
    {
        var request = new LineAnchoredEditRequest("notes.txt", 1, 1, hash, "x");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
    }

    [Fact]
    public async Task RefusesAMissingFileAsNotFound()
    {
        var request = new LineAnchoredEditRequest("missing.txt", 1, 1, Anchor("x"), "y");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(EditRejectReason.NotFound, result.Reason);
    }

    [Fact]
    public async Task RefusesAnEscapingPathWithoutTouchingTheFile()
    {
        _files.CreateSymlink("up", "../outside");
        var request = new LineAnchoredEditRequest("up/x.txt", 1, 1, Anchor("x"), "y");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(EditRejectReason.PathEscape, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task KeepsAMissingFinalNewlineAsItWas()
    {
        _files.SetText("bare.txt", "one\ntwo");
        var request = new LineAnchoredEditRequest("bare.txt", 2, 2, Anchor("two"), "TWO");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("one\nTWO", _files.GetText("bare.txt"));
    }

    [Fact]
    public async Task RefusesANonUtf8FileAsNotText()
    {
        _files.SetBytes("bin.dat", [0x41, 0xFF, 0x0A]);
        var request = new LineAnchoredEditRequest("bin.dat", 1, 1, Anchor("A�"), "x");

        var result = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(EditRejectReason.NotText, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesAnotherFormatsRequestAsMalformed()
    {
        var result = await _tool.ApplyAsync(new WholeFileEditRequest("notes.txt", "x"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(_source, _files.GetText("notes.txt"));
    }
}
