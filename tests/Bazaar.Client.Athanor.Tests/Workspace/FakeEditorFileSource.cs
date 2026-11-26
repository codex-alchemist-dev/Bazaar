namespace Bazaar.Client.Athanor.Tests.Workspace;

/// <summary>In-memory <see cref="IEditorFileSource"/>: documents are added up front, saves always succeed.</summary>
internal sealed class FakeEditorFileSource : IEditorFileSource
{
    private readonly Dictionary<EditorDocumentId, EditorDocument> _documents = [];

    public int OpenCalls { get; private set; }

    public void Add(EditorDocument document) => _documents[document.Id] = document;

    public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        OpenCalls++;
        return Task.FromResult(_documents.TryGetValue(id, out EditorDocument? document) ? document : null);
    }

    public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
    {
        SaveOutcome outcome = new SaveOutcome.Saved(baseRevision + "+1");
        return Task.FromResult(outcome);
    }
}
