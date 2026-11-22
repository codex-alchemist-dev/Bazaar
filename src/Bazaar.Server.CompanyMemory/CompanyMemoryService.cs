using Bazaar.Server.Knowledge.CompanyMemory;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// Company Memory over a decision store (doc 25). Recording validates what the chain will rest on; reading rebuilds the
/// chain from the store each time with <see cref="DecisionChain"/>, so the Vault stays the only copy.
/// </summary>
/// <param name="store">Where decisions live.</param>
/// <param name="index">The derived link index, refreshed after each recorded decision. Null for none.</param>
/// <param name="logger">Told when a refresh fails. The decision itself is already stored, so a failed refresh never fails the record.</param>
public sealed partial class CompanyMemoryService(
    ICompanyDecisionStore store,
    IDecisionLinkIndex? index = null,
    Microsoft.Extensions.Logging.ILogger<CompanyMemoryService>? logger = null,
    IKnowledgePageStore? pages = null) : ICompanyMemory
{
    private readonly ICompanyDecisionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<RecordedDecision> RecordDecisionAsync(RecordDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targets = new List<string>();
        if (request.Supersedes is { Count: > 0 })
        {
            var records = await _store.ListAsync(ct);
            foreach (var reference in request.Supersedes)
            {
                var path = DecisionReference.Resolve(reference, records)
                    ?? throw new ArgumentException($"Decision '{reference}' does not exist, so it cannot be superseded.", nameof(request));
                if (!targets.Contains(path))
                {
                    targets.Add(path);
                }
            }
        }

        // Stored as bare names (0001-slug): the form the Vault writes and DecisionChain reads.
        var names = targets.Select(p => p["decisions/".Length..^".md".Length]).ToList();
        var recorded = await _store.CreateAsync(request with { Supersedes = names }, ct);
        if (!recorded.AlreadyRecorded)
        {
            try
            {
                await RebuildIndexAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (logger is not null)
                {
                    IndexRefreshFailed(logger, exception, recorded.Path);
                }
            }
        }

        return recorded;
    }

    public Task<WrittenKnowledgePage> WritePageAsync(WriteKnowledgePageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Pages().WriteAsync(request, ct);
    }

    public Task<KnowledgePage?> ReadPageAsync(string slug, CancellationToken ct) => Pages().ReadAsync(slug, ct);

    public Task<IReadOnlyList<KnowledgePage>> ListPagesAsync(CancellationToken ct) => Pages().ListAsync(ct);

    private IKnowledgePageStore Pages() =>
        pages ?? throw new InvalidOperationException("No knowledge page store is configured for Company Memory.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "The decision link index refresh failed after decision {Path}; rebuild it to catch up.")]
    private static partial void IndexRefreshFailed(ILogger logger, Exception exception, string path);

    public async Task<int> RebuildIndexAsync(CancellationToken ct)
    {
        var links = DecisionChain.Derive(await _store.ListAsync(ct));
        if (index is not null)
        {
            await index.ReplaceAllAsync(links, ct);
        }

        return links.Count;
    }

    public async Task<DecisionChainView?> GetChainAsync(string reference, CancellationToken ct)
    {
        var records = await _store.ListAsync(ct);
        if (DecisionReference.Resolve(reference, records) is not { } focusPath)
        {
            return null;
        }

        var links = DecisionChain.Derive(records);
        var older = Walk(focusPath, links, link => link.FromDoc, link => link.ToDoc);
        var newer = Walk(focusPath, links, link => link.ToDoc, link => link.FromDoc);

        var inChain = new HashSet<string>(older.Concat(newer).Append(focusPath), StringComparer.Ordinal);
        var chainLinks = links.Where(l => inChain.Contains(l.FromDoc) && inChain.Contains(l.ToDoc)).ToList();

        // Newest = chain members that no chain link replaces. Missing records cannot be current.
        var replaced = chainLinks.Select(l => l.ToDoc).ToHashSet(StringComparer.Ordinal);
        var current = inChain
            .Where(p => !replaced.Contains(p) && records.Any(r => r.Path == p))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new DecisionChainView(records.First(r => r.Path == focusPath), older, newer, chainLinks, current);
    }

    /// <summary>
    /// Breadth-first walk over the links from <paramref name="start"/>, nearest first. A visited set keeps a malformed
    /// cycle in the Vault from looping.
    /// </summary>
    private static List<string> Walk(
        string start,
        IReadOnlyList<DecisionLink> links,
        Func<DecisionLink, string> from,
        Func<DecisionLink, string> to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var next in links.Where(l => from(l) == node).Select(to).Order(StringComparer.Ordinal))
            {
                if (seen.Add(next))
                {
                    result.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        return result;
    }
}
