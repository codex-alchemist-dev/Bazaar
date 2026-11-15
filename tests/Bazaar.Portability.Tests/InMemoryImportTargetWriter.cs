using System.Globalization;

namespace Bazaar.Portability.Tests;

/// <summary>
/// Test-only <see cref="IImportTargetWriter"/>. Keeps written items in memory and fails one chosen batch index.
/// A failing call records nothing, matching the whole-batch contract.
/// </summary>
public sealed class InMemoryImportTargetWriter : IImportTargetWriter
{
    private readonly HashSet<string> _committed = new(StringComparer.Ordinal);

    public List<ImportItem> GcMessages { get; } = [];

    public List<ImportItem> VaultFiles { get; } = [];

    /// <summary>Batch index whose write throws. Null means no failure is injected.</summary>
    public int? FailOnBatchIndex { get; set; }

    public IReadOnlySet<string> CommittedSourceKeys => _committed;

    public void AppendGcMessages(string jobId, int batchIndex, IReadOnlyList<ImportItem> items) =>
        Write(GcMessages, batchIndex, items);

    public void WriteVaultFiles(string jobId, int batchIndex, IReadOnlyList<ImportItem> items) =>
        Write(VaultFiles, batchIndex, items);

    private void Write(List<ImportItem> sink, int batchIndex, IReadOnlyList<ImportItem> items)
    {
        if (batchIndex == FailOnBatchIndex)
        {
            throw new InvalidOperationException("injected failure at batch " + batchIndex.ToString(CultureInfo.InvariantCulture));
        }

        sink.AddRange(items);
        foreach (var item in items)
        {
            _committed.Add(item.Record.SourceKey);
        }
    }
}
