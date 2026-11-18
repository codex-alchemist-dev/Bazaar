using Bazaar.Client.Core.Constellation;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem;

/// <summary>
/// The selected commit of one scene, moved in time order (CommittedAt, then sha). Thin and git-specific: the ordering and
/// the list mirror come from <see cref="AccessibleNodeList"/>. Pure model: no key handling.
/// With no selection, <see cref="MoveNext"/> selects the oldest commit and <see cref="MovePrevious"/> the newest.
/// Moves clamp at both ends (no wrap-around) and return false when the selection does not move.
/// </summary>
public sealed class GitSolarSelection
{
    private readonly Dictionary<string, GitSolarCommit> _commitBySha = new(StringComparer.Ordinal);
    private readonly AccessibleNodeList _list;
    private int _position = -1;

    public GitSolarSelection(GitSolarScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // The list orders by Kind, then Label. Kind carries the commit time (fixed width, so ordinal order is time order);
        // Label carries the sha, which breaks ties deterministically.
        var inputs = new List<AccessibleNodeInput>(scene.Commits.Count);
        foreach (GitSolarCommit commit in scene.Commits)
        {
            _commitBySha.Add(commit.Sha, commit);
            inputs.Add(new AccessibleNodeInput(commit.Sha, commit.Sha, TimeKey(commit.CommittedAt)));
        }

        _list = AccessibleNodeList.Create(inputs, focusedNodeId: null);
    }

    /// <summary>The selected commit's sha, or null when nothing is selected.</summary>
    public string? Current => _position < 0 ? null : _list.Items[_position].NodeId;

    /// <summary>Selects the commit with this sha. Returns false when the scene has no such commit.</summary>
    public bool SelectBySha(string sha)
    {
        if (!_commitBySha.ContainsKey(sha))
        {
            return false;
        }

        foreach (AccessibleNodeItem item in _list.Items)
        {
            if (string.Equals(item.NodeId, sha, StringComparison.Ordinal))
            {
                _position = item.Index;
                return true;
            }
        }

        return false;
    }

    /// <summary>Selects the tip commit of the named branch, looked up in <paramref name="branches"/>. Returns false when the branch or its tip is not in the scene.</summary>
    public bool SelectBranchTip(string name, IReadOnlyList<GitSolarBranch> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        foreach (GitSolarBranch branch in branches)
        {
            if (string.Equals(branch.Name, name, StringComparison.Ordinal))
            {
                return SelectBySha(branch.TipSha);
            }
        }

        return false;
    }

    /// <summary>Moves the selection one commit newer. Returns false at the newest end.</summary>
    public bool MoveNext()
    {
        int next = _list.NextIndex(_position);
        if (next < 0 || next <= _position)
        {
            // Empty list, or the list wrapped from the newest commit back to the oldest: clamp instead.
            return false;
        }

        _position = next;
        return true;
    }

    /// <summary>Moves the selection one commit older. Returns false at the oldest end.</summary>
    public bool MovePrevious()
    {
        int previous = _list.PreviousIndex(_position);
        if (previous < 0 || (_position >= 0 && previous >= _position))
        {
            // Empty list, or the list wrapped from the oldest commit to the newest: clamp instead.
            return false;
        }

        _position = previous;
        return true;
    }

    /// <summary>The argument for <see cref="GitSolarCommands.OpenCommit"/>, or null when nothing is selected.</summary>
    public GitSolarOpenCommit? ToOpenCommit()
    {
        string? sha = Current;
        if (sha is null)
        {
            return null;
        }

        GitSolarCommit commit = _commitBySha[sha];
        return new GitSolarOpenCommit(commit.Sha, commit.Branch);
    }

    private static string TimeKey(DateTimeOffset committedAt) =>
        committedAt.UtcTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
}
