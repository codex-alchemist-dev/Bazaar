using System.Text;
using Bazaar.Contracts.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Git.Reads;

/// <summary>Read settings (doc 52, Settings). Bound from <c>Bazaar:Git</c> by <c>AddBazaarGit</c>.</summary>
public sealed class GitReadOptions
{
    /// <summary>The smallest <see cref="MaxDiffBytes"/> that takes effect.</summary>
    public const int MaxDiffBytesFloor = 65536;

    /// <summary>The largest page size a client may get.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Setting <c>git.max_diff_bytes</c>. Default 2 MiB; values under 64 KiB are raised to the floor.</summary>
    public int MaxDiffBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Setting <c>git.pr_page_size</c>. The default page size; clamped to 1 to 100.</summary>
    public int PrPageSize { get; init; } = 30;

    public int EffectiveMaxDiffBytes => Math.Max(MaxDiffBytes, MaxDiffBytesFloor);

    public int EffectivePageSize => Math.Clamp(PrPageSize, 1, MaxPageSize);
}

/// <summary>The head the client asked about is no longer the PR's head (route result <c>409 head_moved</c>).</summary>
public sealed class HeadMovedException(string currentSha) : Exception("The PR head moved since the client last read it.")
{
    public string CurrentSha { get; } = currentSha;
}

/// <summary>The server-proxied read side of the forge (doc 52, Wire and routes).</summary>
public interface IGitReadService
{
    /// <summary>One page of PRs. <paramref name="pageSize"/> null uses the setting; any size is clamped to 1 to 100. On a forge outage the last cached page comes back with Stale set.</summary>
    Task<GitPrPageDto> ListPrsAsync(RepoRef repo, PrState? state, int page, int? pageSize, CancellationToken cancellationToken);

    Task<GitPrDetailDto> GetPrAsync(PrRef pr, CancellationToken cancellationToken);

    /// <summary>The files keyed by <paramref name="headSha"/>. Throws <see cref="HeadMovedException"/> if the forge head differs.</summary>
    Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    /// <summary>The checks on the PR's current head.</summary>
    Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(PrRef pr, CancellationToken cancellationToken);
}

/// <summary>Maps <see cref="IForgeProvider"/> results to the wire DTOs. Never invents data: an outage with nothing cached is rethrown.</summary>
public sealed class GitReadService : IGitReadService
{
    /// <summary>Most list pages kept for the stale fallback. The oldest entry goes first.</summary>
    public const int MaxCachedPages = 256;

    private readonly IForgeRegistry _registry;
    private readonly GitReadOptions _options;
    private readonly ILogger _logger;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, GitPrPageDto> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    public GitReadService(IForgeRegistry registry, GitReadOptions? options = null, ILogger<GitReadService>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new GitReadOptions();
        _logger = logger ?? NullLogger<GitReadService>.Instance;
    }

    public async Task<GitPrPageDto> ListPrsAsync(RepoRef repo, PrState? state, int page, int? pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var forge = _registry.Resolve(repo.Provider);
        var size = Math.Clamp(pageSize ?? _options.EffectivePageSize, 1, GitReadOptions.MaxPageSize);
        var safePage = Math.Max(page, 1);
        var key = $"{repo.Provider}\n{repo.FullName}\n{state}\n{safePage}\n{size}";
        try
        {
            var result = await forge.ListPrsAsync(repo, new ListPrsQuery(state, safePage, size), cancellationToken).ConfigureAwait(false);
            var dto = new GitPrPageDto([.. result.Items.Select(ToDto)], result.HasMore, Stale: false);
            Remember(key, dto);
            return dto;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GitPrPageDto? cached;
            lock (_cacheLock)
            {
                _cache.TryGetValue(key, out cached);
            }

            if (cached is null)
                throw;

            GitLog.ServingStalePage(_logger, repo.Provider, ex.GetType().Name);
            return cached with { Stale = true };
        }
    }

    public async Task<GitPrDetailDto> GetPrAsync(PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var detail = await _registry.Resolve(pr.Provider).GetPrAsync(pr, cancellationToken).ConfigureAwait(false);
        return new GitPrDetailDto(
            ToDto(detail.Summary),
            detail.Body,
            detail.ChangedFiles,
            detail.Additions,
            detail.Deletions,
            [.. detail.Reviews.Select(r => new GitReviewDto(r.AuthorLogin, ReviewVerdict(r.Verdict), r.HeadSha, r.SubmittedAt.ToUnixTimeMilliseconds()))]);
    }

    public async Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrEmpty(headSha);
        var forge = _registry.Resolve(pr.Provider);
        var current = await forge.GetHeadShaAsync(pr, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, headSha, StringComparison.OrdinalIgnoreCase))
            throw new HeadMovedException(current);

        var files = await forge.GetPrFilesAsync(pr, headSha, cancellationToken).ConfigureAwait(false);
        var max = _options.EffectiveMaxDiffBytes;
        return [.. files.Select(f => ToDto(f, max))];
    }

    public async Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var forge = _registry.Resolve(pr.Provider);
        var head = await forge.GetHeadShaAsync(pr, cancellationToken).ConfigureAwait(false);
        var checks = await forge.GetChecksAsync(pr, head, cancellationToken).ConfigureAwait(false);
        return [.. checks.Select(c => new GitCheckDto(c.Name, c.State.ToString().ToLowerInvariant(), c.HeadSha, c.DetailsUrl))];
    }

    private void Remember(string key, GitPrPageDto dto)
    {
        lock (_cacheLock)
        {
            if (!_cache.ContainsKey(key))
            {
                _cacheOrder.Enqueue(key);
                while (_cacheOrder.Count > MaxCachedPages)
                    _cache.Remove(_cacheOrder.Dequeue());
            }

            _cache[key] = dto;
        }
    }

    private static GitPrSummaryDto ToDto(PrSummary s) => new(
        s.Pr.Provider,
        s.Pr.FullName,
        s.Pr.Number,
        s.Title,
        s.AuthorLogin,
        s.State.ToString().ToLowerInvariant(),
        s.Draft,
        s.HeadSha,
        s.Head.Value,
        s.Base.Value,
        s.UpdatedAt.ToUnixTimeMilliseconds());

    private static string ReviewVerdict(ReviewEvent e) => e switch
    {
        ReviewEvent.Approve => "approve",
        ReviewEvent.RequestChanges => "request_changes",
        _ => "comment",
    };

    private static GitPrFileDto ToDto(PrFile f, int maxBytes)
    {
        var (patch, cut) = CutPatch(f.Patch, maxBytes);
        return new GitPrFileDto(f.Path, f.Status, f.Additions, f.Deletions, patch, f.Truncated || cut);
    }

    /// <summary>Cuts <paramref name="patch"/> to at most <paramref name="maxBytes"/> UTF-8 bytes, never in the middle of a character.</summary>
    public static (string? Patch, bool Cut) CutPatch(string? patch, int maxBytes)
    {
        if (patch is null)
            return (null, false);
        // A char is at most 3 UTF-8 bytes, so a short string needs no byte count.
        if (patch.Length <= maxBytes / 3 || Encoding.UTF8.GetByteCount(patch) <= maxBytes)
            return (patch, false);

        var bytes = Encoding.UTF8.GetBytes(patch);
        var end = maxBytes;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
            end--;
        return (Encoding.UTF8.GetString(bytes, 0, end), true);
    }
}
