namespace Bazaar.Client.Athanor.Sources;

/// <summary>
/// The file source used while the server cannot be reached (doc 52 §6, doc 28). It opens nothing and refuses every save,
/// so the editor shows only cached state. There is no write queue: a refused save keeps its draft in the draft store.
/// </summary>
public sealed class OfflineEditorFileSource : IEditorFileSource
{
    private const string _unavailableReason = "server unavailable";

    /// <summary>Always returns null. Offline, no document can be read, so the caller falls back to its cached copy.</summary>
    public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocument?>(null);

    /// <summary>Always returns <see cref="SaveOutcome.Unavailable"/>. Nothing is written and no revision moves.</summary>
    public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(baseRevision);

        SaveOutcome outcome = new SaveOutcome.Unavailable(_unavailableReason);
        return Task.FromResult(outcome);
    }
}
