using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>
/// The Avalonia <see cref="IEditorCommandPrompts"/> (doc 57 §8): shows an <see cref="EditorPromptView"/> on the window's overlay
/// layer and waits for the answer. A newer prompt cancels one still showing; a cancelled prompt, or a window with no overlay
/// layer, answers null so the command does nothing. Call from the UI thread.
/// </summary>
public sealed class AvaloniaEditorCommandPrompts(TopLevel top) : IEditorCommandPrompts
{
    private EditorPromptView? _current;

    public async Task<string?> AskOpenPathAsync(CancellationToken cancellationToken)
    {
        var answer = await AskAsync(new EditorPromptView("Open document", "path, or workspace:path"), cancellationToken).ConfigureAwait(true);
        return answer?.Text;
    }

    public async Task<NewDocumentRequest?> AskNewDocumentAsync(CancellationToken cancellationToken)
    {
        var languages = LanguageRegistry.Default.Definitions.Select(d => d.LanguageId).Prepend(LanguageIds.PlainText).Distinct(StringComparer.Ordinal).ToList();
        var answer = await AskAsync(new EditorPromptView("New document", "name, or workspace:name", languages), cancellationToken).ConfigureAwait(true);
        return answer is null ? null : new NewDocumentRequest(answer.Text, answer.LanguageId ?? LanguageIds.PlainText);
    }

    private async Task<EditorPromptResult?> AskAsync(EditorPromptView view, CancellationToken cancellationToken)
    {
        if (OverlayLayer.GetOverlayLayer(top) is not { } layer)
        {
            return null;
        }

        _current?.Cancel();
        _current = view;
        layer.Children.Add(view);
        view.FocusInput();
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(view.Cancel));
        try
        {
            return await view.Result.ConfigureAwait(true);
        }
        finally
        {
            layer.Children.Remove(view);
            if (ReferenceEquals(_current, view))
            {
                _current = null;
            }
        }
    }
}
