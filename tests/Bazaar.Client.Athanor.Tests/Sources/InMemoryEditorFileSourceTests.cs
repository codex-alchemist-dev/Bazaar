using Bazaar.Client.Athanor.Sources;

namespace Bazaar.Client.Athanor.Tests.Sources;

public class InMemoryEditorFileSourceTests
{
    private static readonly EditorDocumentId _id = new("ws-1", "src/Program.cs");

    [Fact]
    public async Task OpenReturnsSeededContentLanguageAndRevision()
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_id, "class A {}", LanguageIds.CSharp);

        EditorDocument? document = await source.OpenAsync(_id, CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal(_id, document.Id);
        Assert.Equal("class A {}", document.Content);
        Assert.Equal(LanguageIds.CSharp, document.LanguageId);
        Assert.False(document.ReadOnly);
        Assert.False(string.IsNullOrEmpty(document.Revision));
    }

    [Fact]
    public async Task OpenReturnsNullForUnknownDocument()
    {
        var source = new InMemoryEditorFileSource();

        Assert.Null(await source.OpenAsync(_id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveWithCurrentRevisionIsAcceptedAndMovesRevisionForward()
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_id, "old", LanguageIds.PlainText);
        EditorDocument opened = (await source.OpenAsync(_id, CancellationToken.None))!;

        SaveOutcome outcome = await source.SaveAsync(_id, "new", opened.Revision, CancellationToken.None);

        var saved = Assert.IsType<SaveOutcome.Saved>(outcome);
        Assert.NotEqual(opened.Revision, saved.NewRevision);
        EditorDocument reopened = (await source.OpenAsync(_id, CancellationToken.None))!;
        Assert.Equal("new", reopened.Content);
        Assert.Equal(saved.NewRevision, reopened.Revision);
    }

    [Fact]
    public async Task SaveWithStaleRevisionConflictsAndKeepsServerContent()
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_id, "base", LanguageIds.PlainText);
        EditorDocument opened = (await source.OpenAsync(_id, CancellationToken.None))!;
        await source.SaveAsync(_id, "theirs", opened.Revision, CancellationToken.None);

        SaveOutcome outcome = await source.SaveAsync(_id, "mine", opened.Revision, CancellationToken.None);

        var conflict = Assert.IsType<SaveOutcome.Conflict>(outcome);
        Assert.Equal("theirs", conflict.ServerContent);
        EditorDocument current = (await source.OpenAsync(_id, CancellationToken.None))!;
        Assert.Equal(current.Revision, conflict.ServerRevision);
        Assert.Equal("theirs", current.Content);
    }

    [Fact]
    public async Task ReseedingMakesAnOpenedCopyConflict()
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_id, "one", LanguageIds.PlainText);
        EditorDocument opened = (await source.OpenAsync(_id, CancellationToken.None))!;

        source.Seed(_id, "two", LanguageIds.PlainText);
        SaveOutcome outcome = await source.SaveAsync(_id, "mine", opened.Revision, CancellationToken.None);

        var conflict = Assert.IsType<SaveOutcome.Conflict>(outcome);
        Assert.Equal("two", conflict.ServerContent);
    }

    [Fact]
    public async Task RevisionsStrictlyIncreaseAcrossDocuments()
    {
        var source = new InMemoryEditorFileSource();
        var other = new EditorDocumentId("ws-1", "other.txt");
        source.Seed(_id, "a", LanguageIds.PlainText);
        source.Seed(other, "b", LanguageIds.PlainText);

        EditorDocument first = (await source.OpenAsync(_id, CancellationToken.None))!;
        EditorDocument second = (await source.OpenAsync(other, CancellationToken.None))!;

        Assert.True(long.Parse(second.Revision, System.Globalization.CultureInfo.InvariantCulture)
            > long.Parse(first.Revision, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ReadOnlyDocumentOpensReadOnlyAndRefusesSaves()
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_id, "locked", LanguageIds.PlainText, readOnly: true);
        EditorDocument opened = (await source.OpenAsync(_id, CancellationToken.None))!;

        SaveOutcome outcome = await source.SaveAsync(_id, "edit", opened.Revision, CancellationToken.None);

        Assert.True(opened.ReadOnly);
        var unavailable = Assert.IsType<SaveOutcome.Unavailable>(outcome);
        Assert.Equal("document is read-only", unavailable.Reason);
        Assert.Equal("locked", (await source.OpenAsync(_id, CancellationToken.None))!.Content);
    }

    [Fact]
    public async Task SaveToUnseededDocumentIsUnavailable()
    {
        var source = new InMemoryEditorFileSource();

        SaveOutcome outcome = await source.SaveAsync(_id, "x", "1", CancellationToken.None);

        var unavailable = Assert.IsType<SaveOutcome.Unavailable>(outcome);
        Assert.Equal("document does not exist", unavailable.Reason);
    }

    [Fact]
    public async Task ConcurrentSavesOnOneBaseRevisionAcceptExactlyOne()
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_id, "base", LanguageIds.PlainText);
        EditorDocument opened = (await source.OpenAsync(_id, CancellationToken.None))!;

        const int writers = 64;
        Task<SaveOutcome>[] saves = new Task<SaveOutcome>[writers];
        using var start = new ManualResetEventSlim(false);
        for (int i = 0; i < writers; i++)
        {
            int writer = i;
            saves[i] = Task.Run(() =>
            {
                start.Wait();
                return source.SaveAsync(_id, $"writer {writer}", opened.Revision, CancellationToken.None);
            });
        }

        start.Set();
        SaveOutcome[] outcomes = await Task.WhenAll(saves);

        Assert.Equal(1, outcomes.Count(o => o is SaveOutcome.Saved));
        Assert.Equal(writers - 1, outcomes.Count(o => o is SaveOutcome.Conflict));
        EditorDocument final = (await source.OpenAsync(_id, CancellationToken.None))!;
        Assert.StartsWith("writer ", final.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSeedsAndOpensDoNotCorruptTheTable()
    {
        var source = new InMemoryEditorFileSource();
        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            var id = new EditorDocumentId("ws-1", $"file-{i}.txt");
            source.Seed(id, $"content {i}", LanguageIds.PlainText);
            EditorDocument? document = await source.OpenAsync(id, CancellationToken.None);
            Assert.Equal($"content {i}", document!.Content);
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task SaveRejectsNullContent()
    {
        var source = new InMemoryEditorFileSource();

        await Assert.ThrowsAsync<ArgumentNullException>(() => source.SaveAsync(_id, null!, "1", CancellationToken.None));
    }
}
