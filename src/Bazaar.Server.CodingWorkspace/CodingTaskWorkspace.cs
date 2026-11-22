using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bazaar.Server.CodingGit;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>Port: makes an opened worktree findable by the tool host context. The host binds it to its worktree registry.</summary>
public interface ICodingWorktreeRegistrar
{
    /// <summary>Registers the worktree under the key. Disposing the result removes it.</summary>
    /// <exception cref="InvalidOperationException">The key is already in use.</exception>
    IDisposable Register(string worktreeKey, CodingWorktree worktree);
}

/// <summary>
/// <see cref="ICodingTaskWorkspace"/> over <see cref="CodingGitService"/> (doc 17, "Default repo and per-task worktree flow"). The
/// worktree path, branch, Passport checks and locks all come from the service; this class adds the repo lookup, the task id rules,
/// the existing-worktree refusal and the registration under a random key. A failed open holds and registers nothing.
/// </summary>
public sealed class CodingTaskWorkspace : ICodingTaskWorkspace
{
    private const int _keyBytes = 32;

    private readonly CodingGitService _git;
    private readonly ICodingRepoSource _repos;
    private readonly IWorkShareLayout _layout;
    private readonly ICodingWorktreeRegistrar _registrar;
    private readonly string? _defaultRepoId;
    private readonly ConcurrentDictionary<string, Open> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _opening = new(StringComparer.Ordinal);

    /// <summary>Creates the workspace.</summary>
    /// <param name="git">The worktree service.</param>
    /// <param name="repos">Finds a repo by id.</param>
    /// <param name="layout">The work-share layout, for the existing-worktree and discard checks.</param>
    /// <param name="registrar">Registers the opened worktree.</param>
    /// <param name="defaultRepoId">The operator's default repo (<c>Coding:Harness:Repo</c>); null means every open must name one.</param>
    public CodingTaskWorkspace(
        CodingGitService git,
        ICodingRepoSource repos,
        IWorkShareLayout layout,
        ICodingWorktreeRegistrar registrar,
        string? defaultRepoId)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _defaultRepoId = string.IsNullOrWhiteSpace(defaultRepoId) ? null : defaultRepoId;
    }

    /// <inheritdoc />
    public async Task<CodingTaskWorktree> OpenAsync(
        string agentId, string taskId, string? repoId = null, string? gcId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SafeSegment.IsValid(agentId))
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.InvalidAgent, "The agent id is not a safe id.");
        }

        // The task id is the branch slug and the directory name, used as given: it is never rewritten, so two ids never collapse.
        if (!SafeSegment.IsValid(taskId) || !BranchName.TryParse(BranchName.Prefix + taskId, out _))
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.InvalidTask, "The task id must be one lowercase slug of [a-z0-9._-].");
        }

        var group = string.IsNullOrWhiteSpace(gcId) ? taskId : gcId;
        var wanted = string.IsNullOrWhiteSpace(repoId) ? _defaultRepoId : repoId;
        if (wanted is null)
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.NoRepo, "No repo was named and no default repo is configured.");
        }

        var record = await _repos.FindAsync(wanted, cancellationToken).ConfigureAwait(false)
            ?? throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.UnknownRepo, "The repo is not known to the host.");
        if (!string.Equals(record.Repo.Id, wanted, StringComparison.Ordinal) || !SafeSegment.IsValid(record.Repo.Id))
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.UnknownRepo, "The repo is not known to the host.");
        }

        var slot = $"{agentId}\n{record.Repo.Id}\n{taskId}";
        if (!_opening.TryAdd(slot, 0))
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.AlreadyExists, "A worktree for this task is already open.");
        }

        try
        {
            var path = PathFor(agentId, record.Repo.Id, taskId);

            // The engine's add is idempotent for an existing worktree; a worktree that is already there is refused here instead.
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.AlreadyExists, "A worktree for this task already exists.");
            }

            CodingWorktree worktree;
            try
            {
                worktree = await _git.CreateWorktreeAsync(new CreateWorktreeRequest(agentId, record.Repo, taskId, group, taskId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CodingGitException ex) when (ex.Error == CodingGitError.WorktreeBusy)
            {
                throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.AlreadyExists, "Another writer holds this worktree or branch.", ex);
            }
            catch (CodingGitException ex)
            {
                throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.OpenFailed, $"The worktree could not be opened ({ex.Error}).", ex);
            }

            IDisposable registration;
            string key;
            try
            {
                key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(_keyBytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                registration = _registrar.Register(key, worktree);
            }
            catch
            {
                await worktree.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _byKey[key] = new Open(slot, worktree, registration);
            slot = string.Empty;
            return new CodingTaskWorktree(key, agentId, record.Repo.Id, taskId, worktree.Branch.Value, worktree.Path);
        }
        finally
        {
            if (slot.Length > 0)
            {
                _opening.TryRemove(slot, out _);
            }
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(string worktreeKey, CancellationToken cancellationToken = default)
    {
        if (TakeOpen(worktreeKey) is { } open)
        {
            await ReleaseAsync(open).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DiscardAsync(string worktreeKey, CancellationToken cancellationToken = default)
    {
        if (TakeOpen(worktreeKey) is not { } open)
        {
            return;
        }

        await ReleaseAsync(open).ConfigureAwait(false);
        try
        {
            var worktree = open.Worktree;
            var root = WorkSharePaths.Canonicalize(_layout.GetWorktreeRoot(worktree.AgentId));
            var passport = WorkSharePaths.Canonicalize(_layout.GetPassportFolder(worktree.AgentId));
            var path = WorkSharePaths.Canonicalize(worktree.Path);
            if (!WorkSharePaths.IsEqualOrUnder(path, root) || string.Equals(path, root, WorkSharePaths.Comparison)
                || WorkSharePaths.IsEqualOrUnder(path, passport) || WorkSharePaths.IsEqualOrUnder(passport, path))
            {
                throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.DiscardFailed, "The worktree is not strictly under the agent's work share.");
            }

            // Recursive delete removes a link itself and never follows it.
            if (Directory.Exists(worktree.Path))
            {
                Directory.Delete(worktree.Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CodingGitException)
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.DiscardFailed, "The worktree directory could not be removed.", ex);
        }
    }

    private Open? TakeOpen(string worktreeKey) =>
        !string.IsNullOrEmpty(worktreeKey) && _byKey.TryRemove(worktreeKey, out var open) ? open : null;

    private async Task ReleaseAsync(Open open)
    {
        open.Registration.Dispose();
        await open.Worktree.DisposeAsync().ConfigureAwait(false);
        _opening.TryRemove(open.Slot, out _);
    }

    private string PathFor(string agentId, string repoId, string taskId)
    {
        try
        {
            return WorkSharePaths.Canonicalize(Path.Combine(_layout.GetWorktreeRoot(agentId), repoId, taskId));
        }
        catch (CodingGitException ex)
        {
            throw new CodingTaskWorkspaceException(CodingTaskWorkspaceError.InvalidAgent, "The agent has no usable work share.", ex);
        }
    }

    private sealed record Open(string Slot, CodingWorktree Worktree, IDisposable Registration);
}
