namespace Bazaar.Client.Athanor.Pane;

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
    /// <summary>The prompts for open and new. A head that builds its window after the pane sets them here; null reports the commands unavailable.</summary>
    public IEditorCommandPrompts? Prompts { get; set; } = prompts;

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
        if (Prompts is not { } prompts)
        {
            return new AthanorCommandResult(AthanorCommandStatus.Unavailable, "No document picker is available in this client.");
        }

        string? raw;
        var kind = "document";
        if (isNew)
        {
            var request = await prompts.AskNewDocumentAsync(cancellationToken).ConfigureAwait(true);
            raw = request?.Name;
            if (request is not null)
            {
                kind = $"new {request.LanguageId} document";
            }
        }
        else
        {
            raw = await prompts.AskOpenPathAsync(cancellationToken).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AthanorCommandResult(AthanorCommandStatus.NothingToDo);
        }

        if (!TryResolve(raw.Trim(), out var id, out var problem))
        {
            return new AthanorCommandResult(AthanorCommandStatus.Failed, problem);
        }

        return await pane.OpenAsync(id, cancellationToken).ConfigureAwait(true)
            ? AthanorCommandResult.Done
            : new AthanorCommandResult(AthanorCommandStatus.Failed, $"The {kind} '{id.Path}' was not found or cannot be shown.");
    }

    // Typed text is "path" (in the active document's workspace) or "workspace:path". Text with a slash right after the colon
    // (C:\x) is not split, so the normaliser rejects the drive path.
    private bool TryResolve(string text, out EditorDocumentId id, out string problem)
    {
        id = default;
        problem = string.Empty;
        var workspace = pane.ActiveTab?.Id.WorkspaceId;
        var path = text;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon + 1 < text.Length && text[colon + 1] is not ('\\' or '/'))
        {
            workspace = text[..colon];
            path = text[(colon + 1)..];
        }

        if (workspace is null)
        {
            problem = "Type workspace:path; no document is open to take the workspace from.";
            return false;
        }

        try
        {
            id = EditorDocumentIds.Create(workspace, path);
            return true;
        }
        catch (ArgumentException)
        {
            problem = $"'{text}' is not a valid workspace path.";
            return false;
        }
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
