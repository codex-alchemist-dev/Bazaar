using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;

namespace Bazaar.Server.Tests.Memory.Tools;

/// <summary>
/// A Vault fake: files in a dictionary, one commit per batch, and lexical search by counting distinct query terms
/// found in a note's title and body. The real VaultWriter and index replace it later.
/// </summary>
internal sealed class InMemoryMemoryVault : IMemoryVault
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<MemoryBatch> _commits = [];

    /// <summary>Every file currently in the fake, by path.</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>Every batch committed, in order.</summary>
    public IReadOnlyList<MemoryBatch> Commits => _commits;

    /// <summary>Places a file directly, as if committed by someone else (for example a quarantined note).</summary>
    public void Seed(string path, string text) => _files[path] = text;

    /// <inheritdoc />
    public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var item in batch.Items)
        {
            _files[item.Path] = item.Content;
        }

        _commits.Add(batch);
        return Task.FromResult(MemoryCommitResult.Success($"fake-{_commits.Count:D4}"));
    }

    /// <inheritdoc />
    public Task<string?> ReadNoteAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(path.Value, out var text) ? text : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<VaultPath>> ListAsync(string areaPrefix, CancellationToken ct)
    {
        IReadOnlyList<VaultPath> paths = _files.Keys
            .Where(key => key.StartsWith(areaPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(VaultPath.Parse)
            .ToList();
        return Task.FromResult(paths);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RankedHit>> SearchLexicalAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct)
    {
        var terms = queryText
            .Split([' ', '\n', '\t', ',', '.', ':', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        IReadOnlyList<RankedHit> hits = _files
            .Where(pair => pair.Key.StartsWith(areaPrefix, StringComparison.Ordinal))
            .Select(pair => (Path: pair.Key, Score: Score(pair.Value, terms)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Take(maxCandidates)
            .Select(candidate => new RankedHit(candidate.Path, candidate.Score))
            .ToList();
        return Task.FromResult(hits);
    }

    /// <inheritdoc />
    public Task<VaultPath?> FindNoteByIdAsync(string noteId, CancellationToken ct)
    {
        foreach (var (path, text) in _files)
        {
            var parsed = NoteDocument.Parse(text);
            if (parsed.IsSuccess && parsed.Document!.Note.Id == noteId)
            {
                return Task.FromResult<VaultPath?>(VaultPath.Parse(path));
            }
        }

        return Task.FromResult<VaultPath?>(null);
    }

    private static double Score(string text, IReadOnlyList<string> terms)
    {
        var words = new HashSet<string>(
            text.Split([' ', '\n', '\t', ',', '.', ':', ';', '-', '"', '`'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant()),
            StringComparer.Ordinal);
        return terms.Count(words.Contains);
    }
}
