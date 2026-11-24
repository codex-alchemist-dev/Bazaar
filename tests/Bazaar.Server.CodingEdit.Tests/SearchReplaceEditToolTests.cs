using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class SearchReplaceEditToolTests
{
    private const string _source = "int Total = 1;\nint Count = 2;\n";

    private readonly InMemoryWorktreeFiles _files = new();
    private readonly SearchReplaceEditTool _tool;

    public SearchReplaceEditToolTests()
    {
        _files.SetText("Calc.cs", _source);
        _tool = new SearchReplaceEditTool(_files);
    }

    [Fact]
    public async Task ReplacesTheSingleExactOccurrence()
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("Calc.cs", "Count = 2", "Count = 3"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("int Total = 1;\nint Count = 3;\n", _files.GetText("Calc.cs"));
        Assert.Equal(1, result.ChangedLines);
    }

    [Fact]
    public async Task ReplacesAMultiLineSearch()
    {
        var result = await _tool.ApplyAsync(
            new SearchReplaceEditRequest("Calc.cs", "int Total = 1;\nint Count = 2;", "int Sum = 0;"),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("int Sum = 0;\n", _files.GetText("Calc.cs"));
    }

    [Fact]
    public async Task RefusesAnAmbiguousSearchAndWritesNothing()
    {
        _files.SetText("dup.txt", "x = 1\nx = 1\n");

        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("dup.txt", "x = 1", "x = 2"), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(EditRejectReason.AmbiguousMatch, result.Reason);
        Assert.Equal("x = 1\nx = 1\n", _files.GetText("dup.txt"));
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task CountsOverlappingOccurrencesAsAmbiguous()
    {
        _files.SetText("runs.txt", "aaa");

        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("runs.txt", "aa", "b"), CancellationToken.None);

        Assert.Equal(EditRejectReason.AmbiguousMatch, result.Reason);
        Assert.Equal("aaa", _files.GetText("runs.txt"));
    }

    [Fact]
    public async Task RefusesASearchThatDoesNotOccur()
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("Calc.cs", "Missing", "x"), CancellationToken.None);

        Assert.Equal(EditRejectReason.NoMatch, result.Reason);
        Assert.Equal(_source, _files.GetText("Calc.cs"));
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task ARepeatedEditDoesNotApplyTwice()
    {
        var request = new SearchReplaceEditRequest("Calc.cs", "Total = 1", "Total = 9");

        var first = await _tool.ApplyAsync(request, CancellationToken.None);
        var second = await _tool.ApplyAsync(request, CancellationToken.None);

        Assert.True(first.Applied);
        Assert.Equal(EditRejectReason.NoMatch, second.Reason);
        Assert.Equal("int Total = 9;\nint Count = 2;\n", _files.GetText("Calc.cs"));
        Assert.Equal(1, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesAnEmptySearchAsMalformed()
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("Calc.cs", "", "x"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(_source, _files.GetText("Calc.cs"));
    }

    [Fact]
    public async Task RefusesAMissingFileAsNotFound()
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("none.cs", "a", "b"), CancellationToken.None);

        Assert.Equal(EditRejectReason.NotFound, result.Reason);
    }

    [Theory]
    [InlineData("../Calc.cs")]
    [InlineData("/abs/Calc.cs")]
    public async Task RefusesEscapingPaths(string path)
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest(path, "int", "long"), CancellationToken.None);

        Assert.Equal(EditRejectReason.PathEscape, result.Reason);
        Assert.Equal(0, _files.WriteCount);
    }

    [Fact]
    public async Task RefusesAFileThatChangedAfterItWasRead()
    {
        var changedOnce = false;
        _files.AfterReadHook = path =>
        {
            if (!changedOnce)
            {
                changedOnce = true;
                _files.SetText(path, "int Other = 0;\n");
            }
        };

        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("Calc.cs", "Count", "Sum"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Conflict, result.Reason);
        Assert.Equal("int Other = 0;\n", _files.GetText("Calc.cs"));
    }

    [Fact]
    public async Task RefusesAReplacementWithAnUnpairedSurrogateAsMalformed()
    {
        var result = await _tool.ApplyAsync(new SearchReplaceEditRequest("Calc.cs", "Count", "\uDC00"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal(_source, _files.GetText("Calc.cs"));
    }
}
