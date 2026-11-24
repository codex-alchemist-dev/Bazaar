using Bazaar.Client.Athanor.Sources;

namespace Bazaar.Client.Athanor.Tests.Sources;

public class DiffSessionTests
{
    [Theory]
    [InlineData("src/Program.cs", LanguageIds.CSharp)]
    [InlineData("web/app.tsx", LanguageIds.TypeScript)]
    [InlineData("Dockerfile", LanguageIds.Dockerfile)]
    [InlineData("docs/readme.md", LanguageIds.Markdown)]
    public async Task ChoosesLanguageFromPathName(string path, string expected)
    {
        var host = new RecordingDiffHost();

        await DiffSession.ShowAsync(host, "a", "b", path, LanguageRegistry.Default, CancellationToken.None);

        ShownDiff shown = Assert.Single(host.Diffs);
        Assert.Equal(expected, shown.LanguageId);
        Assert.Equal("a", shown.Original);
        Assert.Equal("b", shown.Modified);
    }

    [Fact]
    public async Task UsesShebangOfModifiedTextWhenNameDoesNotDecide()
    {
        var host = new RecordingDiffHost();

        await DiffSession.ShowAsync(host, "", "#!/usr/bin/env python3\r\nprint(1)\n", "tools/run", LanguageRegistry.Default, CancellationToken.None);

        Assert.Equal(LanguageIds.Python, Assert.Single(host.Diffs).LanguageId);
    }

    [Fact]
    public async Task NameWinsOverShebang()
    {
        var host = new RecordingDiffHost();

        await DiffSession.ShowAsync(host, "", "#!/usr/bin/env python3\n", "build.ps1", LanguageRegistry.Default, CancellationToken.None);

        Assert.Equal(LanguageIds.PowerShell, Assert.Single(host.Diffs).LanguageId);
    }

    [Fact]
    public async Task UnknownNameWithoutHintIsPlainText()
    {
        var host = new RecordingDiffHost();

        await DiffSession.ShowAsync(host, "x", "y", "notes/data.unknown", LanguageRegistry.Default, CancellationToken.None);

        Assert.Equal(LanguageIds.PlainText, Assert.Single(host.Diffs).LanguageId);
    }

    [Fact]
    public async Task UsesTheRegistryPassedIn()
    {
        var registry = new LanguageRegistry([new LanguageDefinition("custom", ["zz"], [], [])]);
        var host = new RecordingDiffHost();

        await DiffSession.ShowAsync(host, "", "", "thing.zz", registry, CancellationToken.None);

        Assert.Equal("custom", Assert.Single(host.Diffs).LanguageId);
    }

    [Fact]
    public async Task RejectsNullArguments()
    {
        var host = new RecordingDiffHost();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DiffSession.ShowAsync(null!, "a", "b", "x.cs", LanguageRegistry.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DiffSession.ShowAsync(host, null!, "b", "x.cs", LanguageRegistry.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DiffSession.ShowAsync(host, "a", null!, "x.cs", LanguageRegistry.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DiffSession.ShowAsync(host, "a", "b", null!, LanguageRegistry.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DiffSession.ShowAsync(host, "a", "b", "x.cs", null!, CancellationToken.None));
        Assert.Empty(host.Diffs);
    }

    [Fact]
    public void ConflictDiffPutsServerOnTheLeftAndMineOnTheRight()
    {
        var conflict = new SaveOutcome.Conflict("9", "server text");

        ConflictDiff diff = ConflictDiff.From(conflict, "my text");

        Assert.Equal("server text", diff.Original);
        Assert.Equal("my text", diff.Modified);
    }

    [Fact]
    public async Task ConflictDiffFeedsDiffSessionWithServerAsOriginal()
    {
        var host = new RecordingDiffHost();
        ConflictDiff diff = ConflictDiff.From(new SaveOutcome.Conflict("9", "server"), "mine");

        await DiffSession.ShowAsync(host, diff.Original, diff.Modified, "lib/x.cs", LanguageRegistry.Default, CancellationToken.None);

        ShownDiff shown = Assert.Single(host.Diffs);
        Assert.Equal("server", shown.Original);
        Assert.Equal("mine", shown.Modified);
        Assert.Equal(LanguageIds.CSharp, shown.LanguageId);
    }

    [Fact]
    public void ConflictDiffRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ConflictDiff.From(null!, "mine"));
        Assert.Throws<ArgumentNullException>(() => ConflictDiff.From(new SaveOutcome.Conflict("1", "s"), null!));
    }
}
