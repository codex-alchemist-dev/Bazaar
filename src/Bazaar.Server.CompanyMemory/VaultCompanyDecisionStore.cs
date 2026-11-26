using Bazaar.Server.Knowledge.CompanyMemory;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// The decision store over a real Vault: reads <c>decisions/*.md</c> at the current head and creates records through
/// <see cref="VaultWriter"/>, which owns numbering, trailers and the index hook (doc 18). Nothing is cached, so the Vault
/// stays the single copy.
/// </summary>
public sealed class VaultCompanyDecisionStore(IGitProvider git, VaultWriter writer, string repoPath) : ICompanyDecisionStore
{
    private const string _decisionsPrefix = "decisions/";

    private readonly IGitProvider _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly VaultWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly string _repoPath = !string.IsNullOrWhiteSpace(repoPath) ? repoPath : throw new ArgumentException("A Vault path is required.", nameof(repoPath));

    public async Task<IReadOnlyList<DecisionRecord>> ListAsync(CancellationToken ct)
    {
        var head = await _git.HeadAsync(_repoPath, ct);
        var records = new List<DecisionRecord>();
        foreach (var file in await _git.ListFilesAsync(_repoPath, head, ct))
        {
            if (!IsDecisionFile(file))
            {
                continue;
            }

            var record = VaultDecisionParser.TryParse(file, await _git.ReadTextAsync(_repoPath, head, file, ct));
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    public async Task<RecordedDecision> CreateAsync(RecordDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _writer.CreateDecisionAsync(
            new DecisionDraft(request.DocId, request.Slug, request.Title, request.Body, request.AgentId, request.Supersedes, request.DecidedIn),
            ct);
        var number = result.DecisionNumber ?? throw new InvalidOperationException("The Vault wrote a decision without a number.");

        var head = await _git.HeadAsync(_repoPath, ct);
        var prefix = $"{_decisionsPrefix}{number:D4}-";
        var path = (await _git.ListFilesAsync(_repoPath, head, ct)).FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Decision {number} is missing from the Vault after it was written.");
        return new RecordedDecision(path, number, result.Outcome == VaultEditOutcome.UpToDate);
    }

    private static bool IsDecisionFile(string path) =>
        path.StartsWith(_decisionsPrefix, StringComparison.Ordinal)
        && path.EndsWith(".md", StringComparison.Ordinal)
        && path.IndexOf('/', _decisionsPrefix.Length) < 0;
}
