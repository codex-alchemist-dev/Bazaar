using System.Text.RegularExpressions;

namespace Bazaar.Server.CodingGit;

/// <summary>Settings for <see cref="CodingGitService"/>.</summary>
public sealed class CodingGitOptions
{
    /// <summary>
    /// How long a writer lease lasts without activity. Every operation renews it. Default 2 hours, the harness task
    /// time limit (doc 17); a crashed holder frees the worktree after this.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>Request to open a worktree for a task.</summary>
/// <param name="AgentId">The agent that will write in it.</param>
/// <param name="Repo">The repository.</param>
/// <param name="TaskSlug">The task slug; the branch becomes <c>agent/&lt;TaskSlug&gt;</c>.</param>
/// <param name="GcId">The group chat the work belongs to (commit trailer).</param>
/// <param name="TaskId">The task id (commit trailer).</param>
public sealed record CreateWorktreeRequest(string AgentId, RepoRef Repo, string TaskSlug, string GcId, string TaskId);

/// <summary>What the agent wants committed.</summary>
/// <param name="Subject">One-line subject.</param>
/// <param name="Body">Optional body.</param>
/// <param name="Model">The model credited as <c>Co-Authored-By</c>.</param>
public sealed record CommitInput(string Subject, string? Body, ModelIdentity Model);

/// <summary>What the agent wants pushed. The pull request is always a draft; there is no option for anything else.</summary>
/// <param name="Title">Pull request title.</param>
/// <param name="Body">Pull request body.</param>
/// <param name="Branch">The branch to push. Null means the worktree's own branch; any other value is refused.</param>
public sealed record PushDraftPrInput(string Title, string Body, string? Branch = null);

/// <summary>
/// A worktree held by one writer. Dispose it (or let the lease expire) to free the worktree and its branch for the
/// next writer. It carries no credentials.
/// </summary>
public sealed class CodingWorktree : IAsyncDisposable
{
    private readonly IWorktreeLease[] _leases;
    private int _disposed;

    internal CodingWorktree(CreateWorktreeRequest request, BranchName branch, string path, IWorktreeLease[] leases)
    {
        AgentId = request.AgentId;
        Repo = request.Repo;
        Branch = branch;
        Path = path;
        GcId = request.GcId;
        TaskId = request.TaskId;
        _leases = leases;
    }

    /// <summary>The writing agent.</summary>
    public string AgentId { get; }

    /// <summary>The repository.</summary>
    public RepoRef Repo { get; }

    /// <summary>The branch checked out here, always <c>agent/&lt;task&gt;</c>.</summary>
    public BranchName Branch { get; }

    /// <summary>The canonical worktree path on the agent's work share.</summary>
    public string Path { get; }

    /// <summary>The group chat id written in commit trailers.</summary>
    public string GcId { get; }

    /// <summary>The task id written in commit trailers.</summary>
    public string TaskId { get; }

    /// <summary>True until disposed.</summary>
    public bool IsActive => Volatile.Read(ref _disposed) == 0 && _leases.All(l => l.IsHeld);

    internal SemaphoreSlim OperationGate { get; } = new(1, 1);

    internal bool TryRenewAll() => Volatile.Read(ref _disposed) == 0 && _leases.All(l => l.TryRenew());

    /// <summary>Releases the locks. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var lease in _leases)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Policy wrapper for agent worktrees, commits and draft pull requests (doc 17, Git workflow). It adds the rules the
/// git mechanics do not carry: the <c>agent/&lt;task&gt;</c> branch name, one writer per worktree and branch, the work
/// share versus Passport folder placement rule, the commit trailers (never <c>Signed-off-by</c>), the push gate, and
/// draft-only pull requests. The git mechanics and the credentialed push sit behind <see cref="ICodingGitEngine"/> and
/// <see cref="IHostPushPort"/>; no token ever crosses either port.
/// </summary>
public sealed partial class CodingGitService(
    IWorkShareLayout layout,
    ICodingGitEngine engine,
    IHostPushPort pushPort,
    IPushGate gate,
    IAgentGitIdentity identity,
    IWorktreeLocks locks,
    CodingGitOptions? options = null)
{
    private const int _maxTitle = 256;
    private const int _maxPrBody = 60_000;

    private readonly CodingGitOptions _options = options ?? new CodingGitOptions();

    /// <summary>
    /// Opens a worktree on <c>agent/&lt;task&gt;</c> under the agent's work share. Fails fast with
    /// <see cref="CodingGitError.WorktreeBusy"/> when another writer holds the worktree or the branch.
    /// </summary>
    public async Task<CodingWorktree> CreateWorktreeAsync(CreateWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        var branch = BranchName.ForTask(request.TaskSlug);
        var path = ResolveWorktreePath(request, branch);

        var comparisonKey = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? path.ToUpperInvariant() : path;
        var leases = new List<IWorktreeLease>(2);
        try
        {
            leases.Add(await AcquireAsync($"path:{comparisonKey}", cancellationToken).ConfigureAwait(false));
            leases.Add(await AcquireAsync($"branch:{request.Repo.Id}/{branch.Value}", cancellationToken).ConfigureAwait(false));
            await engine.AddWorktreeAsync(new AddWorktreeRequest(request.Repo, branch, path, request.AgentId), cancellationToken).ConfigureAwait(false);
            return new CodingWorktree(request, branch, path, [.. leases]);
        }
        catch
        {
            // Includes cancellation: never leave a lease behind when the worktree handle was not handed out.
            foreach (var lease in leases)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Commits the worktree's pending changes as the agent's GitHub App identity.</summary>
    public Task<CommitId> CommitAsync(CodingWorktree worktree, CommitInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(input);
        return RunExclusiveAsync(worktree, async () =>
        {
            var commit = CommitMessageBuilder.Build(input.Subject, input.Body, worktree.AgentId, worktree.GcId, worktree.TaskId, input.Model);
            var author = await identity.GetAuthorAsync(worktree.AgentId, cancellationToken).ConfigureAwait(false);
            return await engine.CommitAsync(new EngineCommitRequest(worktree.Path, commit.Message, commit.Trailers, author), cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>Returns the worktree's changes against the repository's default branch.</summary>
    public Task<GitDiff> DiffAgainstBaseAsync(CodingWorktree worktree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        return RunExclusiveAsync(worktree, () => engine.DiffAgainstBaseAsync(worktree.Path, worktree.Repo.DefaultBranch, cancellationToken));
    }

    /// <summary>
    /// Pushes the worktree's <c>agent/*</c> branch and opens a draft pull request, but only after the push gate allows
    /// it. The default branch and any non-agent branch are refused before the gate and the port are called.
    /// </summary>
    public Task<PullRequestRef> PushDraftPrAsync(CodingWorktree worktree, PushDraftPrInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(input);
        return RunExclusiveAsync(worktree, async () =>
        {
            var branch = DefaultBranchGuard.RequireAgentBranch(input.Branch ?? worktree.Branch.Value, worktree.Repo.DefaultBranch);
            if (branch != worktree.Branch)
            {
                throw new CodingGitException(CodingGitError.BranchRefused, "A worktree can only push its own branch.");
            }

            var title = CleanTitle(input.Title);
            var body = CleanPrBody(input.Body);

            var decision = await gate.EvaluateAsync(new PushGateRequest(worktree.AgentId, worktree.Repo, branch, worktree.GcId, worktree.TaskId), cancellationToken).ConfigureAwait(false);
            switch (decision.Verdict)
            {
                case PushVerdict.Allow:
                    break;
                case PushVerdict.NeedsApproval:
                    throw new CodingGitException(CodingGitError.PushNeedsApproval, "The push needs a recorded approval first; nothing was pushed.");
                default:
                    throw new CodingGitException(CodingGitError.PushDenied, "The push gate denied the push; nothing was pushed.");
            }

            var pr = await pushPort.PushDraftPrAsync(new HostPushRequest(worktree.AgentId, worktree.Repo, branch, title, body), cancellationToken).ConfigureAwait(false);
            if (pr is null || !pr.IsDraft)
            {
                throw new CodingGitException(CodingGitError.PushResultInvalid, "The push port did not return a draft pull request.");
            }

            return pr;
        });
    }

    private static async Task<T> RunExclusiveAsync<T>(CodingWorktree worktree, Func<Task<T>> operation)
    {
        if (!worktree.TryRenewAll())
        {
            throw new CodingGitException(CodingGitError.LeaseLost, "The worktree was released or its lease expired.");
        }

        if (!worktree.OperationGate.Wait(0))
        {
            throw new CodingGitException(CodingGitError.WorktreeBusy, "Another operation is already running on this worktree.");
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            worktree.OperationGate.Release();
        }
    }

    private async Task<IWorktreeLease> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        return await locks.TryAcquireAsync(key, _options.LeaseDuration, cancellationToken).ConfigureAwait(false)
            ?? throw new CodingGitException(CodingGitError.WorktreeBusy, "Another writer holds this worktree or branch.");
    }

    private string ResolveWorktreePath(CreateWorktreeRequest request, BranchName branch)
    {
        var root = WorkSharePaths.Canonicalize(layout.GetWorktreeRoot(request.AgentId));
        var passport = WorkSharePaths.Canonicalize(layout.GetPassportFolder(request.AgentId));

        // The share itself must not be the Passport folder, inside it, or hold it.
        if (WorkSharePaths.IsEqualOrUnder(root, passport) || WorkSharePaths.IsEqualOrUnder(passport, root))
        {
            throw new CodingGitException(CodingGitError.WorktreeOnPassportFolder, "The work share and the Passport folder must be separate.");
        }

        // Layout matches the host workspace: <root>/<repoId>/<task>.
        var path = WorkSharePaths.Canonicalize(Path.Combine(root, request.Repo.Id, branch.TaskSlug));
        // Passport first, so a link into the Passport folder reports the sharper error.
        if (WorkSharePaths.IsEqualOrUnder(path, passport) || WorkSharePaths.IsEqualOrUnder(passport, path))
        {
            throw new CodingGitException(CodingGitError.WorktreeOnPassportFolder, "A worktree can never be on, under or around the Passport folder.");
        }

        if (!WorkSharePaths.IsEqualOrUnder(path, root) || string.Equals(path, root, WorkSharePaths.Comparison))
        {
            throw new CodingGitException(CodingGitError.WorktreeOutsideWorkShare, "The worktree resolves outside the agent's work share.");
        }

        return path;
    }

    private static void ValidateRequest(CreateWorktreeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Repo);
        if (request.AgentId is null || !IdPattern().IsMatch(request.AgentId)
            || request.GcId is null || !IdPattern().IsMatch(request.GcId)
            || request.TaskId is null || !IdPattern().IsMatch(request.TaskId))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "Agent, GC and task ids must be 1 to 128 characters of [A-Za-z0-9._:-].");
        }

        if (request.Repo.Id is null || !RepoIdPattern().IsMatch(request.Repo.Id) || string.IsNullOrWhiteSpace(request.Repo.DefaultBranch))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "The repository id or default branch is invalid.");
        }
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > _maxTitle || title.Any(char.IsControl))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "The pull request title must be one non-empty line of at most 256 characters.");
        }

        SecretGuard.ThrowIfSecret(title);
        return title.Trim();
    }

    private static string CleanPrBody(string body)
    {
        body ??= string.Empty;
        if (body.Length > _maxPrBody)
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "The pull request body is too long.");
        }

        SecretGuard.ThrowIfSecret(body);
        return body;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    // No leading dot or dash, so the id is never "." or ".." and never an option-looking name.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoIdPattern();
}
