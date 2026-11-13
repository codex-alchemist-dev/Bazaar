namespace Bazaar.Client.Core.Ryusei;

/// <summary>
/// The pause and resume actions of the micro schedule pane (doc 38, layer 2). A head supplies the implementation through
/// <see cref="Bazaar.Client.Core.Panes.PaneModuleContext.With{T}"/>. The server owns the authority rules: a refused
/// change throws, and the pane then keeps the row as it was. No wire request exists yet for these actions, so the
/// first-party heads supply none and the pane shows its buttons disabled.
/// </summary>
public interface IRyuseiTriggerActions
{
    /// <summary>Pauses one trigger. Throws when the server refuses or cannot be reached.</summary>
    Task PauseAsync(string triggerId, CancellationToken ct);

    /// <summary>Resumes one paused trigger. Throws when the server refuses or cannot be reached.</summary>
    Task ResumeAsync(string triggerId, CancellationToken ct);
}
