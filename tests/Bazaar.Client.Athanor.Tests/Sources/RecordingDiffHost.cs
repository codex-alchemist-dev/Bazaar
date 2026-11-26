namespace Bazaar.Client.Athanor.Tests.Sources;

/// <summary>A diff shown through <see cref="RecordingDiffHost"/>.</summary>
internal sealed record ShownDiff(string Original, string Modified, string LanguageId);

/// <summary>An <see cref="IEditorHost"/> that records <see cref="ShowDiffAsync"/> calls and does nothing else.</summary>
internal sealed class RecordingDiffHost : IEditorHost
{
    public List<ShownDiff> Diffs { get; } = [];

    public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FocusAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken)
    {
        Diffs.Add(new ShownDiff(original, modified, languageId));
        return Task.CompletedTask;
    }

    public event EventHandler<EditorChange>? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler<EditorDocumentId>? SaveRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<EditorKeyEvent>? KeyPressed
    {
        add { }
        remove { }
    }

    public event EventHandler? Ready
    {
        add { }
        remove { }
    }

    public event EventHandler<EditorCrashReason>? Crashed
    {
        add { }
        remove { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
