using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>
/// The default model. It keeps the latest snapshot, orders its commits by time and moves a selection along that order.
/// It handles snapshots and disconnects only: <see cref="Scene"/> stays null and frames are refused, so positions come
/// from the scene-building model.
/// </summary>
internal sealed class SimpleGitSolarModel : IGitSolarModel
{
    private GitSolarSnapshot? _snapshot;
    private IReadOnlyList<int> _order = [];
    private Dictionary<string, int> _indexBySha = new(StringComparer.Ordinal);
    private string? _selectedSha;

    public GitSolarScene? Scene => null;

    public bool IsStale { get; private set; }

    public string? SelectedSha => _selectedSha;

    public bool Apply(GitSolarUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        switch (update)
        {
            case GitSolarUpdate.Snapshot snapshot:
                return ApplySnapshot(snapshot.Value);
            case GitSolarUpdate.Disconnected:
                IsStale = true;
                return true;
            default:
                return false;
        }
    }

    public bool SelectBySha(string sha)
    {
        if (string.IsNullOrEmpty(sha) || !_indexBySha.ContainsKey(sha))
        {
            return false;
        }

        _selectedSha = sha;
        return true;
    }

    public bool SelectBranchTip(string name)
    {
        var branch = _snapshot?.Branches.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return branch is not null && SelectBySha(branch.TipSha);
    }

    public void MoveNext()
    {
        var position = PositionOfSelection();
        SelectPosition(position < 0 ? 0 : Math.Min(position + 1, _order.Count - 1));
    }

    public void MovePrevious()
    {
        var position = PositionOfSelection();
        SelectPosition(position < 0 ? 0 : Math.Max(position - 1, 0));
    }

    public GitSolarOpenCommit? ToOpenCommit()
    {
        if (_snapshot is null || _selectedSha is null || !_indexBySha.TryGetValue(_selectedSha, out var index))
        {
            return null;
        }

        var commit = _snapshot.Commits[index];
        return new GitSolarOpenCommit(commit.Sha, commit.Branch);
    }

    private bool ApplySnapshot(GitSolarSnapshot snapshot)
    {
        if (_snapshot is not null && snapshot.GraphVersion < _snapshot.GraphVersion)
        {
            return false;
        }

        _snapshot = snapshot;
        _order = GitSolarTimeline.NewestFirst(snapshot);
        _indexBySha = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < snapshot.Commits.Count; index++)
        {
            _indexBySha.TryAdd(snapshot.Commits[index].Sha, index);
        }

        IsStale = false;
        if (_selectedSha is null || !_indexBySha.ContainsKey(_selectedSha))
        {
            _selectedSha = _order.Count > 0 ? snapshot.Commits[_order[0]].Sha : null;
        }

        return true;
    }

    private int PositionOfSelection()
    {
        if (_snapshot is null || _selectedSha is null)
        {
            return -1;
        }

        for (var position = 0; position < _order.Count; position++)
        {
            if (string.Equals(_snapshot.Commits[_order[position]].Sha, _selectedSha, StringComparison.Ordinal))
            {
                return position;
            }
        }

        return -1;
    }

    private void SelectPosition(int position)
    {
        if (_snapshot is not null && position >= 0 && position < _order.Count)
        {
            _selectedSha = _snapshot.Commits[_order[position]].Sha;
        }
    }
}
