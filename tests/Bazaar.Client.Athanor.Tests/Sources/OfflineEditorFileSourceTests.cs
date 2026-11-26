using Bazaar.Client.Athanor.Sources;

namespace Bazaar.Client.Athanor.Tests.Sources;

public class OfflineEditorFileSourceTests
{
    private static readonly EditorDocumentId _id = new("ws-1", "notes.md");

    [Fact]
    public async Task OpenReturnsNullWhileOffline()
    {
        var source = new OfflineEditorFileSource();

        Assert.Null(await source.OpenAsync(_id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveIsRefusedAsUnavailableWithoutQueueing()
    {
        var source = new OfflineEditorFileSource();

        SaveOutcome first = await source.SaveAsync(_id, "draft", "7", CancellationToken.None);
        SaveOutcome second = await source.SaveAsync(_id, "draft", "7", CancellationToken.None);

        var unavailable = Assert.IsType<SaveOutcome.Unavailable>(first);
        Assert.Equal("server unavailable", unavailable.Reason);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task SaveRejectsNullContent()
    {
        var source = new OfflineEditorFileSource();

        await Assert.ThrowsAsync<ArgumentNullException>(() => source.SaveAsync(_id, null!, "7", CancellationToken.None));
    }
}
