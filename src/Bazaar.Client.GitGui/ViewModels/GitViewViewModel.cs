namespace Bazaar.Client.GitGui.ViewModels;

/// <summary>A command the Git view asks the host to run. The view never opens another module itself (doc 52, Constellation link).</summary>
public sealed record GitCommandRequest(string CommandId, string Parameter);

/// <summary>
/// The Git view: repos, the branches of the selected repo and its recent commits (doc 52). Lists come from
/// <see cref="IGitRepoSource"/>. A branch or commit row can ask the host to open it in the Constellation; that goes out as a
/// command request, so this module never references the Constellation module.
/// </summary>
public sealed class GitViewViewModel : GitViewModelBase
{
    private readonly IGitRepoSource _source;
    private IReadOnlyList<GitRepoRow> _repos = [];
    private IReadOnlyList<GitBranchRow> _branches = [];
    private IReadOnlyList<GitCommitRow> _commits = [];
    private GitRepoRow? _selectedRepo;
    private string? _selectedBranch;
    private string? _error;
    private string? _selectedCommitSha;

    public GitViewViewModel(IGitRepoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>Raised when a row asks the host to run <c>git.open-in-constellation</c>.</summary>
    public event EventHandler<GitCommandRequest>? CommandRequested;

    public IReadOnlyList<GitRepoRow> Repos { get => _repos; private set => Set(ref _repos, value); }

    public IReadOnlyList<GitBranchRow> Branches { get => _branches; private set => Set(ref _branches, value); }

    public IReadOnlyList<GitCommitRow> Commits { get => _commits; private set => Set(ref _commits, value); }

    public GitRepoRow? SelectedRepo { get => _selectedRepo; private set => Set(ref _selectedRepo, value); }

    public string? SelectedBranch { get => _selectedBranch; private set => Set(ref _selectedBranch, value); }

    public string? SelectedCommitSha { get => _selectedCommitSha; private set => Set(ref _selectedCommitSha, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Error = null;
        try
        {
            Repos = await _source.ListReposAsync(cancellationToken).ConfigureAwait(true);
            if (SelectedRepo is null && Repos.Count > 0)
            {
                await SelectRepoAsync(Repos[0], cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Selects a repo, loads its branches, and selects its default branch when it has one.</summary>
    public async Task SelectRepoAsync(GitRepoRow repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        Error = null;
        try
        {
            var branches = await _source.ListBranchesAsync(repo, cancellationToken).ConfigureAwait(true);
            SelectedRepo = repo;
            Branches = branches;
            var branch = branches.FirstOrDefault(b => b.Name == repo.DefaultBranch) ?? (branches.Count > 0 ? branches[0] : null);
            if (branch is null)
            {
                SelectedBranch = null;
                Commits = [];
                return;
            }

            await SelectBranchAsync(branch.Name, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public async Task SelectBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (SelectedRepo is not { } repo)
        {
            return;
        }

        Error = null;
        try
        {
            Commits = await _source.ListCommitsAsync(repo, branch, cancellationToken).ConfigureAwait(true);
            SelectedBranch = branch;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Asks the host to show a branch name or commit sha as a node in the Constellation (<c>git.open-in-constellation</c>).</summary>
    public void OpenInConstellation(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        CommandRequested?.Invoke(this, new GitCommandRequest(GitGuiCommandIds.OpenInConstellation, reference));
    }

    /// <summary>
    /// Handles <c>git.open-commit</c>: selects the commit with this sha (full sha or a prefix of at least 7 characters) when the
    /// loaded list has it. Returns false when it does not, so the host can load the right repo and branch first.
    /// </summary>
    public bool OpenCommit(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        var match = sha.Length >= 7
            ? Commits.FirstOrDefault(c => c.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedCommitSha = match?.Sha;
        return match is not null;
    }
}
