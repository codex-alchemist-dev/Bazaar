using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.CodingGit;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>
/// <see cref="ISubagentWorktreeAllocator"/> over <see cref="CodingGitService"/> (doc 17, "Workspace sources"). A writer child gets its
/// own worktree on branch <c>agent/&lt;parentTask&gt;-&lt;subagent&gt;</c> under the parent agent's work share, behind the service's two
/// host locks (path and branch), so no two writers ever share one. A read-only child gets the parent's worktree path marked
/// read-only and no new git state. An unknown parent, a path outside the share, an id already holding a lease, or any failure
/// from the service throws and leaves nothing held. Release frees the locks and keeps the directory and branch.
/// </summary>
public sealed class GitSubagentWorktreeAllocator : ISubagentWorktreeAllocator
{
    private const int _maxSubagentPart = 40;

    private readonly CodingGitService _git;
    private readonly IWorkShareLayout _layout;
    private readonly ISubagentParentWorkSource _parents;
    private readonly ConcurrentDictionary<string, Live> _byLease = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _subagents = new(StringComparer.Ordinal);

    /// <summary>Creates the allocator.</summary>
    /// <param name="git">The worktree service.</param>
    /// <param name="layout">The work-share layout, used to confirm a read-only path is inside the parent's share.</param>
    /// <param name="parents">Resolves a parent session to its work.</param>
    public GitSubagentWorktreeAllocator(CodingGitService git, IWorkShareLayout layout, ISubagentParentWorkSource parents)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _parents = parents ?? throw new ArgumentNullException(nameof(parents));
    }

    /// <inheritdoc />
    public async Task<WorktreeLease> AllocateAsync(WorktreeAllocation request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SubagentId) || string.IsNullOrWhiteSpace(request.ParentId))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "A subagent id and a parent id are required.");
        }

        var parent = await _parents.FindAsync(request.ParentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The parent session has no worktree to derive from.");
        if (!_subagents.TryAdd(request.SubagentId, 0))
        {
            throw new InvalidOperationException("This subagent already holds a worktree lease.");
        }

        try
        {
            var live = request.ReadOnly
                ? ReadOnlyView(parent)
                : await OpenWriterAsync(request.SubagentId, parent, cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            live = live with { SubagentId = request.SubagentId };
            _byLease[id] = live;
            return new WorktreeLease(id, live.Path, request.ReadOnly);
        }
        catch
        {
            _subagents.TryRemove(request.SubagentId, out _);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(WorktreeLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // Only a lease this allocator issued, with the path it issued, is released; anything else is a no-op.
        if (!_byLease.TryGetValue(lease.Id, out var live) || !string.Equals(live.Path, lease.Path, StringComparison.Ordinal)
            || !_byLease.TryRemove(lease.Id, out live))
        {
            return;
        }

        _subagents.TryRemove(live.SubagentId, out _);
        if (live.Worktree is not null)
        {
            await live.Worktree.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Live ReadOnlyView(SubagentParentWork parent)
    {
        var root = WorkSharePaths.Canonicalize(_layout.GetWorktreeRoot(parent.AgentId));
        var passport = WorkSharePaths.Canonicalize(_layout.GetPassportFolder(parent.AgentId));
        if (string.IsNullOrWhiteSpace(parent.WorktreePath) || !Path.IsPathFullyQualified(parent.WorktreePath))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "The parent worktree path is not absolute.");
        }

        var path = WorkSharePaths.Canonicalize(parent.WorktreePath);
        if (WorkSharePaths.IsEqualOrUnder(path, passport) || WorkSharePaths.IsEqualOrUnder(passport, path))
        {
            throw new CodingGitException(CodingGitError.WorktreeOnPassportFolder, "A worktree can never be on, under or around the Passport folder.");
        }

        if (!WorkSharePaths.IsEqualOrUnder(path, root) || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), WorkSharePaths.Comparison))
        {
            throw new CodingGitException(CodingGitError.WorktreeOutsideWorkShare, "The parent worktree is outside the agent's work share.");
        }

        return new Live(string.Empty, path, null);
    }

    private async Task<Live> OpenWriterAsync(string subagentId, SubagentParentWork parent, CancellationToken cancellationToken)
    {
        var slug = ChildSlug(parent.TaskSlug, subagentId);
        var worktree = await _git.CreateWorktreeAsync(
            new CreateWorktreeRequest(parent.AgentId, parent.Repo, slug, parent.GcId, parent.TaskId),
            cancellationToken).ConfigureAwait(false);
        return new Live(string.Empty, worktree.Path, worktree);
    }

    /// <summary>
    /// The child's task slug: the parent's slug, a dash, a sanitised copy of the subagent id, and 8 hex characters of the id's hash,
    /// so two ids that sanitise alike still get different branches. At most <see cref="BranchName.MaxTaskLength"/> characters.
    /// </summary>
    internal static string ChildSlug(string parentSlug, string subagentId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subagentId)))[..8];
        var part = new StringBuilder();
        foreach (var c in subagentId.ToLowerInvariant())
        {
            if (part.Length == _maxSubagentPart)
            {
                break;
            }

            part.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-');
        }

        var child = part.ToString().Trim('-');
        var suffix = child.Length == 0 ? hash : $"{child}-{hash}";
        var room = BranchName.MaxTaskLength - suffix.Length - 1;
        var head = (parentSlug ?? string.Empty).Length > room ? parentSlug![..room] : parentSlug ?? string.Empty;
        return $"{head}-{suffix}";
    }

    private sealed record Live(string SubagentId, string Path, CodingWorktree? Worktree);
}
