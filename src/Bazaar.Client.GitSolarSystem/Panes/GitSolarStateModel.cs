using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Constellation.Rendering.Core;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>
/// The pane model backed by <see cref="GitSolarState"/>: the scene, the latest layout frame and the stale flag come from it.
/// The selection is a <see cref="GitSolarSelection"/> over the current scene. A new snapshot rebuilds the selection and keeps
/// the selected commit when the new history still holds it; otherwise the newest commit is selected.
/// </summary>
internal sealed class GitSolarStateModel : IGitSolarModel
{
    private readonly GitSolarState _state = new();
    private GitSolarSelection? _selection;
    private IReadOnlyList<GitSolarBranch> _branches = [];

    public GitSolarScene? Scene => _state.Scene;

    public RenderFrame? LatestFrame => _state.LatestFrame;

    public bool IsStale => _state.IsStale;

    public string? SelectedSha => _selection?.Current;

    public bool Apply(GitSolarUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!_state.Apply(update))
        {
            return false;
        }

        if (update is GitSolarUpdate.Snapshot snapshot)
        {
            Rebuild(snapshot.Value);
        }

        return true;
    }

    public bool SelectBySha(string sha) => _selection is not null && _selection.SelectBySha(sha);

    public bool SelectBranchTip(string name) => _selection is not null && _selection.SelectBranchTip(name, _branches);

    public GitSolarOpenCommit? ToOpenCommit() => _selection?.ToOpenCommit();

    private void Rebuild(GitSolarSnapshot snapshot)
    {
        string? previous = SelectedSha;
        _branches = snapshot.Branches;
        if (Scene is not { } scene)
        {
            _selection = null;
            return;
        }

        _selection = new GitSolarSelection(scene);
        if (previous is null || !_selection.SelectBySha(previous))
        {
            // With no selection, MovePrevious lands on the newest commit (the selection's time order runs oldest first).
            _selection.MovePrevious();
        }
    }
}
