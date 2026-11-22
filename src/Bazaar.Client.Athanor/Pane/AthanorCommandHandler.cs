namespace Bazaar.Client.Athanor.Pane;

/// <summary>Head-supplied pickers for the commands that need a path (doc 57 §8). A null result means the user cancelled.</summary>
public interface IEditorCommandPrompts
{
    /// <summary>Asks which existing document to open.</summary>
    Task<EditorDocumentId?> PickDocumentAsync(CancellationToken cancellationToken);

    /// <summary>Asks for the path of a new document.</summary>
    Task<EditorDocumentId?> PickNewDocumentAsync(CancellationToken cancellationToken);
}

/// <summary>How a command ended.</summary>
public enum AthanorCommandStatus
{
    Done,
    NothingToDo,
    Unavailable,
    Failed,
}

/// <summary>The outcome of one command, with a line the shell can show when it is not <see cref="AthanorCommandStatus.Done"/>.</summary>
public sealed record AthanorCommandResult(AthanorCommandStatus Status, string? Reason = null)
{
    public static AthanorCommandResult Done { get; } = new(AthanorCommandStatus.Done);
}

/// <summary>
/// Executes the Athanor commands for the pane (doc 57 §8). The head forwards a mnemonic command id here. Every command
/// uses a non-Ctrl mnemonic; none claims a Ctrl chord.
/// </summary>
public sealed class AthanorCommandHandler(AthanorPaneViewModel pane, IEditorCommandPrompts? prompts = null)
{
    /// <summary>True when the id is one of this module's commands.</summary>
    public static bool Handles(string commandId) => AthanorClientModule.CommandIds.Contains(commandId, StringComparer.Ordinal);

    public async Task<AthanorCommandResult> InvokeAsync(string commandId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        if (pane.IsEmpty)
        {
            return new AthanorCommandResult(AthanorCommandStatus.Unavailable, AthanorPaneViewModel.EmptyMessage);
        }

        try
        {
            return commandId switch
            {
                AthanorClientModule.SaveCommandId => await SaveAsync(cancellationToken).ConfigureAwait(true),
                AthanorClientModule.OpenCommandId => await OpenAsync(isNew: false, cancellationToken).ConfigureAwait(true),
                AthanorClientModule.NewCommandId => await OpenAsync(isNew: true, cancellationToken).ConfigureAwait(true),
                AthanorClientModule.DiffCommandId => await DiffAsync(cancellationToken).ConfigureAwait(true),
                _ => new AthanorCommandResult(AthanorCommandStatus.Unavailable, $"Unknown command '{commandId}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new AthanorCommandResult(AthanorCommandStatus.Failed, ex.Message);
        }
    }

    private async Task<AthanorCommandResult> SaveAsync(CancellationToken cancellationToken)
    {
        var outcome = await pane.SaveActiveAsync(cancellationToken).ConfigureAwait(true);
        return outcome switch
        {
            null => new AthanorCommandResult(AthanorCommandStatus.NothingToDo, "No document is open."),
            SaveOutcome.Saved => AthanorCommandResult.Done,
            SaveOutcome.Conflict => new AthanorCommandResult(AthanorCommandStatus.Failed, "The server has a newer revision of this document."),
            SaveOutcome.Unavailable unavailable => new AthanorCommandResult(AthanorCommandStatus.Failed, "Not saved: " + unavailable.Reason),
            _ => new AthanorCommandResult(AthanorCommandStatus.Failed, "Not saved."),
        };
    }

    private async Task<AthanorCommandResult> OpenAsync(bool isNew, CancellationToken cancellationToken)
    {
        if (prompts is null)
        {
            return new AthanorCommandResult(AthanorCommandStatus.Unavailable, "No document picker is available in this client.");
        }

        var id = isNew
            ? await prompts.PickNewDocumentAsync(cancellationToken).ConfigureAwait(true)
            : await prompts.PickDocumentAsync(cancellationToken).ConfigureAwait(true);
        if (id is null)
        {
            return new AthanorCommandResult(AthanorCommandStatus.NothingToDo);
        }

        return await pane.OpenAsync(id.Value, cancellationToken).ConfigureAwait(true)
            ? AthanorCommandResult.Done
            : new AthanorCommandResult(AthanorCommandStatus.Failed, $"'{id.Value.Path}' was not found or cannot be shown.");
    }

    private async Task<AthanorCommandResult> DiffAsync(CancellationToken cancellationToken)
    {
        if (pane.ActiveTab is null)
        {
            return new AthanorCommandResult(AthanorCommandStatus.NothingToDo, "No document is open.");
        }

        return await pane.ShowActiveDiffAsync(cancellationToken).ConfigureAwait(true)
            ? AthanorCommandResult.Done
            : new AthanorCommandResult(AthanorCommandStatus.Failed, "The server copy could not be read.");
    }
}
