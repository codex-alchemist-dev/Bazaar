using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;

namespace Bazaar.Server.CompanyKnowledge.Tests;

internal sealed class FakeVault : IMemoryVault
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public List<MemoryBatch> Batches { get; } = [];

    public bool FailCommits { get; set; }

    public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
    {
        if (FailCommits)
        {
            return Task.FromResult(MemoryCommitResult.Failure("disk"));
        }

        Batches.Add(batch);
        foreach (var item in batch.Items)
        {
            Files[item.Path] = item.Content;
        }

        return Task.FromResult(MemoryCommitResult.Success("sha"));
    }

    public Task<string?> ReadNoteAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(path.Value, out var text) ? text : null);

    public Task<IReadOnlyList<VaultPath>> ListAsync(string areaPrefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VaultPath>>([.. Files.Keys.Where(k => k.StartsWith(areaPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(VaultPath.Parse)]);

    public Task<IReadOnlyList<RankedHit>> SearchLexicalAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RankedHit>>([]);

    public Task<VaultPath?> FindNoteByIdAsync(string noteId, CancellationToken ct)
    {
        foreach (var (path, text) in Files)
        {
            if (NoteDocument.Parse(text).Document is { } doc && doc.Note.Id == noteId)
            {
                return Task.FromResult<VaultPath?>(VaultPath.Parse(path));
            }
        }

        return Task.FromResult<VaultPath?>(null);
    }
}
