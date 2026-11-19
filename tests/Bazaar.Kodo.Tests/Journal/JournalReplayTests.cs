using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Tests.Journal;

public sealed class JournalReplayTests
{
    [Fact]
    public void ReplayTwiceProducesNoDuplicates()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 3, count: 10);
        HashSet<string> applied = [];

        IReadOnlyList<JournalRecord> first = JournalReplay.Apply(records, applied);
        IReadOnlyList<JournalRecord> second = JournalReplay.Apply(records, applied);

        Assert.Equal(records, first);
        Assert.Empty(second);
        Assert.Equal(records.Count, applied.Count);
    }

    [Fact]
    public void ReplaySkipsRecordsAlreadyPresent()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 14, count: 10);
        HashSet<string> applied = records.Take(4).Select(JournalChain.KeyOf).ToHashSet();

        IReadOnlyList<JournalRecord> newlyApplied = JournalReplay.Apply(records, applied);

        Assert.Equal(records.Skip(4), newlyApplied);
        Assert.Equal(records.Count, applied.Count);
    }

    [Fact]
    public void RecordRepeatedWithinOneBatchIsAppliedOnce()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 15, count: 5);
        HashSet<string> applied = [];

        IReadOnlyList<JournalRecord> newlyApplied = JournalReplay.Apply(records.Concat(records), applied);

        Assert.Equal(records, newlyApplied);
    }

    [Fact]
    public void ReplayOfSpillIsIdempotent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kodo-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "spill.log");
            using (JournalSpillWriter writer = JournalSpillWriter.Open(path))
            {
                foreach (JournalRecord record in JournalSamples.Chain(seed: 16, count: 8))
                {
                    writer.Append(record.At, record.Kind, record.Payload);
                }
            }

            HashSet<string> applied = [];
            IReadOnlyList<JournalRecord> spilled = JournalSpill.Read(path).Records;

            Assert.Equal(8, JournalReplay.Apply(spilled, applied).Count);
            Assert.Empty(JournalReplay.Apply(spilled, applied));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
