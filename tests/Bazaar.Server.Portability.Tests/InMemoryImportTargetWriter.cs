using System.Globalization;

namespace Bazaar.Server.Portability.Tests;

/// <summary>
/// Test-only <see cref="IImportTargetWriter"/>. Keeps written items in memory and fails one chosen batch index.
/// A failing call records nothing, matching the whole-batch contract.
/// </summary>
public sealed class InMemoryImportTargetWriter : IImportTargetWriter
{
    private readonly HashSet<(string JobId, string Key)> _written = [];

    public List<ImportItem> GcMessages { get; } = [];

    public List<ImportItem> VaultFiles { get; } = [];

    public List<ImportItem> KanbanTasks { get; } = [];

    /// <summary>Batch index whose write throws. Null means no failure is injected.</summary>
    public int? FailOnBatchIndex { get; set; }

    public Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(_written.Where(w => w.JobId == jobId).Select(w => w.Key).ToHashSet(StringComparer.Ordinal));

    public Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct)
    {
        Write(GcMessages, batchIndex, items);
        return Task.CompletedTask;
    }

    public Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct)
    {
        Write(VaultFiles, batchIndex, items);
        return Task.CompletedTask;
    }

    public Task AppendKanbanTasksAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct)
    {
        Write(KanbanTasks, batchIndex, items);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ReadKanbanSourceKeysAsync(string jobId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(KanbanTasks.Where(i => i.Provenance.JobId == jobId).Select(i => i.Record.SourceKey).ToList());

    public Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(GcMessages.Where(i => i.Provenance.JobId == jobId).Select(i => i.Record.SourceKey).ToList());

    public Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(VaultFiles.Where(i => i.Provenance.JobId == jobId).Select(i => i.Record.SourceKey).ToList());

    private void Write(List<ImportItem> sink, int batchIndex, IReadOnlyList<ImportItem> items)
    {
        if (batchIndex == FailOnBatchIndex)
        {
            throw new InvalidOperationException("injected failure at batch " + batchIndex.ToString(CultureInfo.InvariantCulture));
        }

        sink.AddRange(items);
        foreach (var item in items)
        {
            _written.Add((item.Provenance.JobId, item.Record.SourceKey));
        }
    }
}
