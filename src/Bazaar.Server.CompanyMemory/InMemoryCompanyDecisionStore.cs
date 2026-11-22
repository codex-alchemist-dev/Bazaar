using System.Text.RegularExpressions;
using Bazaar.Server.Knowledge.CompanyMemory;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// A decision store that keeps records in memory: the fallback for a host with no Vault, and the fake for tests. It applies
/// the same slug and idempotency rules as the Vault writer so behaviour matches.
/// </summary>
public sealed partial class InMemoryCompanyDecisionStore : ICompanyDecisionStore
{
    private readonly object _gate = new();
    private readonly List<DecisionRecord> _records = [];
    private readonly Dictionary<Guid, RecordedDecision> _byDocId = [];

    public Task<IReadOnlyList<DecisionRecord>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DecisionRecord>>([.. _records]);
        }
    }

    public Task<RecordedDecision> CreateAsync(RecordDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SlugPattern().IsMatch(request.Slug ?? string.Empty))
        {
            throw new ArgumentException("Slug must be lowercase letters, digits and single hyphens.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Body);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);

        lock (_gate)
        {
            if (_byDocId.TryGetValue(request.DocId, out var existing))
            {
                return Task.FromResult(existing with { AlreadyRecorded = true });
            }

            var number = _records.Count + 1;
            var path = $"decisions/{number:D4}-{request.Slug}.md";
            _records.Add(new DecisionRecord(
                path,
                "proposed",
                [.. (request.Supersedes ?? []).Select(DecisionReference.ToPath)],
                null,
                request.DecidedIn));
            var recorded = new RecordedDecision(path, number, false);
            _byDocId[request.DocId] = recorded;
            return Task.FromResult(recorded);
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
