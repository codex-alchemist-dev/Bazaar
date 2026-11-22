using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui;

/// <summary>A fake <see cref="IGitGuiClient"/> for tests and fixtures. It holds PRs in memory and never touches a network.</summary>
public sealed class InMemoryGitGuiClient : IGitGuiClient
{
    private readonly Dictionary<string, Entry> _prs = new(StringComparer.Ordinal);

    /// <summary>When true, list pages report <see cref="GitPrPageDto.Stale"/>.</summary>
    public bool Stale { get; set; }

    /// <summary>When set, every call throws it. Lets a test exercise the error path.</summary>
    public Exception? Failure { get; set; }

    public int ListCalls { get; private set; }

    public int FileCalls { get; private set; }

    /// <summary>Adds or replaces a PR with its files and checks.</summary>
    public void Set(GitPrDetailDto detail, IEnumerable<GitPrFileDto> files, IEnumerable<GitCheckDto> checks)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(checks);
        _prs[Key(detail.Summary.Provider, detail.Summary.Repo, detail.Summary.Number)] = new Entry(detail, [.. files], [.. checks]);
    }

    /// <summary>Moves a PR's head, as a force-push would. Files and checks stay as they were.</summary>
    public void MoveHead(string provider, string repo, long number, string newSha)
    {
        var entry = Find(provider, repo, number);
        entry.Detail = entry.Detail with { Summary = entry.Detail.Summary with { HeadSha = newSha } };
    }

    /// <summary>Replaces the files and checks of a PR.</summary>
    public void Replace(string provider, string repo, long number, IEnumerable<GitPrFileDto> files, IEnumerable<GitCheckDto> checks)
    {
        var entry = Find(provider, repo, number);
        entry.Files = [.. files];
        entry.Checks = [.. checks];
    }

    public Task<GitPrPageDto> ListPrsAsync(string provider, string repo, string state, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        ListCalls++;
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var matching = _prs.Values
            .Select(entry => entry.Detail.Summary)
            .Where(s => string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(state, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(s.State, state, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.UpdatedAtMs)
            .ThenByDescending(s => s.Number)
            .ToList();

        var items = matching.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new GitPrPageDto(items, matching.Count > page * pageSize, Stale));
    }

    public Task<GitPrDetailDto> GetPrAsync(string provider, string repo, long number, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        return Task.FromResult(Find(provider, repo, number).Detail);
    }

    public Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(string provider, string repo, long number, string headSha, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        FileCalls++;
        var entry = Find(provider, repo, number);
        if (!string.Equals(entry.Detail.Summary.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new HeadMovedException(entry.Detail.Summary.HeadSha);
        }

        return Task.FromResult<IReadOnlyList<GitPrFileDto>>(entry.Files);
    }

    public Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(string provider, string repo, long number, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        return Task.FromResult<IReadOnlyList<GitCheckDto>>(Find(provider, repo, number).Checks);
    }

    /// <summary>When set, <see cref="MergeAsync"/> throws it after recording the request. Lets a test exercise a refusal.</summary>
    public Exception? MergeFailure { get; set; }

    /// <summary>Every merge request this fake received, in order. Holds the signed approval, so use it in tests only.</summary>
    public List<GitMergeRequestDto> MergeRequests { get; } = [];

    public Task<GitMergedDto> MergeAsync(string provider, string repo, long number, GitMergeRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfFailed();
        MergeRequests.Add(request);
        if (MergeFailure is not null)
        {
            throw MergeFailure;
        }

        var entry = Find(provider, repo, number);
        var current = entry.Detail.Summary.HeadSha;
        if (!string.Equals(current, request.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new HeadMovedException(current);
        }

        entry.Detail = entry.Detail with { Summary = entry.Detail.Summary with { State = "merged" } };
        return Task.FromResult(new GitMergedDto(request.HeadSha));
    }

    private static string Key(string provider, string repo, long number) =>
        $"{provider.ToLowerInvariant()}/{repo.ToLowerInvariant()}#{number}";

    private Entry Find(string provider, string repo, long number) =>
        _prs.TryGetValue(Key(provider, repo, number), out var entry)
            ? entry
            : throw new KeyNotFoundException($"No PR {provider}/{repo}#{number} in the fake client.");

    private void ThrowIfFailed()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }

    private sealed class Entry(GitPrDetailDto detail, List<GitPrFileDto> files, List<GitCheckDto> checks)
    {
        public GitPrDetailDto Detail { get; set; } = detail;

        public List<GitPrFileDto> Files { get; set; } = files;

        public List<GitCheckDto> Checks { get; set; } = checks;
    }
}
