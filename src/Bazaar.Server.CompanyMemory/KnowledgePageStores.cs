using System.Text.RegularExpressions;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Markdown;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.CompanyMemory;

/// <summary>Slug rule shared by the page stores: lowercase words joined by single hyphens, the same as decision slugs.</summary>
internal static partial class PageSlug
{
    public const string Folder = "company/memory/";

    public static void Validate(string slug)
    {
        if (!Pattern().IsMatch(slug ?? string.Empty))
        {
            throw new ArgumentException("Slug must be lowercase letters, digits and single hyphens.", nameof(slug));
        }
    }

    public static string PathOf(string slug) => Folder + slug + ".md";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Knowledge pages in memory: the fallback without a Vault, and the fake for tests.</summary>
public sealed class InMemoryKnowledgePageStore : IKnowledgePageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Guid DocId, KnowledgePage Page)> _pages = new(StringComparer.Ordinal);

    public Task<WrittenKnowledgePage> WriteAsync(WriteKnowledgePageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        PageSlug.Validate(request.Slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Body);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);

        var path = PageSlug.PathOf(request.Slug);
        var page = new KnowledgePage(request.Slug, path, request.Title, request.Body.TrimEnd());
        lock (_gate)
        {
            if (_pages.Any(p => p.Value.DocId == request.DocId && p.Key != request.Slug))
            {
                throw new InvalidOperationException("This document id is already used by another page.");
            }

            if (_pages.TryGetValue(request.Slug, out var existing))
            {
                if (existing.DocId != request.DocId)
                {
                    throw new InvalidOperationException("A different document already lives at this path.");
                }

                if (existing.Page == page)
                {
                    return Task.FromResult(new WrittenKnowledgePage(path, PageWriteOutcome.UpToDate));
                }

                _pages[request.Slug] = (request.DocId, page);
                return Task.FromResult(new WrittenKnowledgePage(path, PageWriteOutcome.Updated));
            }

            _pages[request.Slug] = (request.DocId, page);
            return Task.FromResult(new WrittenKnowledgePage(path, PageWriteOutcome.Created));
        }
    }

    public Task<KnowledgePage?> ReadAsync(string slug, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_pages.TryGetValue(slug, out var entry) ? entry.Page : null);
        }
    }

    public Task<IReadOnlyList<KnowledgePage>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<KnowledgePage>>([.. _pages.Values.Select(v => v.Page).OrderBy(p => p.Slug, StringComparer.Ordinal)]);
        }
    }
}

/// <summary>
/// Knowledge pages in the Vault: writes through <see cref="IVaultPageWriter"/> (doc 18, "Page writes") and reads the files at
/// the current head. Nothing is cached, so the Vault stays the single copy.
/// </summary>
public sealed class VaultKnowledgePageStore(IGitProvider git, IVaultPageWriter writer, string repoPath) : IKnowledgePageStore
{
    private readonly IGitProvider _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IVaultPageWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly string _repoPath = !string.IsNullOrWhiteSpace(repoPath) ? repoPath : throw new ArgumentException("A Vault path is required.", nameof(repoPath));

    public async Task<WrittenKnowledgePage> WriteAsync(WriteKnowledgePageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        PageSlug.Validate(request.Slug);

        var path = PageSlug.PathOf(request.Slug);
        var result = await _writer.CreateOrUpdatePageAsync(
            new VaultPageDraft(request.DocId, path, request.Title, "memory", request.Body, request.AgentId, request.GcId), ct);
        var outcome = result.Outcome switch
        {
            VaultPageOutcome.Created => PageWriteOutcome.Created,
            VaultPageOutcome.Updated => PageWriteOutcome.Updated,
            _ => PageWriteOutcome.UpToDate,
        };
        return new WrittenKnowledgePage(path, outcome);
    }

    public async Task<KnowledgePage?> ReadAsync(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        try
        {
            PageSlug.Validate(slug);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var head = await _git.HeadAsync(_repoPath, ct);
        var path = PageSlug.PathOf(slug);
        if (!(await _git.ListFilesAsync(_repoPath, head, ct)).Contains(path, StringComparer.Ordinal))
        {
            return null;
        }

        return Parse(slug, path, await _git.ReadTextAsync(_repoPath, head, path, ct));
    }

    public async Task<IReadOnlyList<KnowledgePage>> ListAsync(CancellationToken ct)
    {
        var head = await _git.HeadAsync(_repoPath, ct);
        var pages = new List<KnowledgePage>();
        foreach (var file in await _git.ListFilesAsync(_repoPath, head, ct))
        {
            if (!file.StartsWith(PageSlug.Folder, StringComparison.Ordinal) || !file.EndsWith(".md", StringComparison.Ordinal)
                || file.IndexOf('/', PageSlug.Folder.Length) >= 0)
            {
                continue;
            }

            var slug = file[PageSlug.Folder.Length..^3];
            pages.Add(Parse(slug, file, await _git.ReadTextAsync(_repoPath, head, file, ct)));
        }

        return [.. pages.OrderBy(p => p.Slug, StringComparer.Ordinal)];
    }

    private static KnowledgePage Parse(string slug, string path, string source)
    {
        var split = FrontMatterSplitter.Split(source);
        var title = split.Yaml is null ? slug : VaultFrontMatterReader.Read(split.Yaml).Title;
        return new KnowledgePage(slug, path, title, source[split.BodyStart..].TrimStart('\r', '\n').TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
