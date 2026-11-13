using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Tests.Journal;

public sealed class JournalSpillTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kodo-journal-" + Guid.NewGuid().ToString("N"));

    public JournalSpillTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private string SpillPath => Path.Combine(_directory, "spill.log");

    [Fact]
    public void SpillWriteThenReadRoundTrips()
    {
        List<JournalRecord> written = JournalSamples.Chain(seed: 5, count: 25);
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
        {
            foreach (JournalRecord record in written)
            {
                writer.Append(record.At, record.Kind, record.Payload);
            }
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);

        Assert.False(read.TornTailIgnored);
        Assert.Equal(new FileInfo(SpillPath).Length, read.ValidLength);
        Assert.Equal(written.Count, read.Records.Count);
        Assert.True(JournalChain.Verify(read.Records).IsValid);
        for (int index = 0; index < written.Count; index++)
        {
            Assert.Equal(written[index].Sequence, read.Records[index].Sequence);
            Assert.Equal(written[index].At, read.Records[index].At);
            Assert.Equal(written[index].Kind, read.Records[index].Kind);
            Assert.Equal(written[index].Payload, read.Records[index].Payload);
            Assert.Equal(written[index].Hash, read.Records[index].Hash);
        }
    }

    [Fact]
    public void TornFinalLineIsIgnored()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 6, count: 6);
        WriteSpill(records.Take(5));
        byte[] partial = JournalSpill.FormatLine(records[5]);
        long validLength = new FileInfo(SpillPath).Length;
        using (FileStream file = new(SpillPath, FileMode.Append, FileAccess.Write))
        {
            // Half of the sixth line, with no LF: a crash in the middle of a write.
            file.Write(partial, 0, partial.Length / 2);
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);

        Assert.True(read.TornTailIgnored);
        Assert.Equal(validLength, read.ValidLength);
        Assert.Equal(5, read.Records.Count);
        Assert.True(JournalChain.Verify(read.Records).IsValid);
    }

    [Fact]
    public void WriterCutsTornTailAndContinuesTheChain()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 8, count: 6);
        WriteSpill(records.Take(5));
        byte[] partial = JournalSpill.FormatLine(records[5]);
        using (FileStream file = new(SpillPath, FileMode.Append, FileAccess.Write))
        {
            file.Write(partial, 0, partial.Length / 2);
        }

        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
        {
            Assert.Equal(5, writer.NextSequence);
            writer.Append(records[5].At, records[5].Kind, records[5].Payload);
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);
        Assert.False(read.TornTailIgnored);
        Assert.Equal(6, read.Records.Count);
        Assert.True(JournalChain.Verify(read.Records).IsValid);
        Assert.Equal(records[5].Hash, read.Records[5].Hash);
    }

    [Fact]
    public void ReopeningContinuesTheChainAcrossWriters()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 12, count: 5);
        using (JournalSpillWriter first = JournalSpillWriter.Open(SpillPath))
        {
            foreach (JournalRecord record in records.Take(3))
            {
                first.Append(record.At, record.Kind, record.Payload);
            }
        }

        using (JournalSpillWriter second = JournalSpillWriter.Open(SpillPath))
        {
            foreach (JournalRecord record in records.Skip(3))
            {
                second.Append(record.At, record.Kind, record.Payload);
            }
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);
        Assert.Equal(5, read.Records.Count);
        Assert.True(JournalChain.Verify(read.Records).IsValid);
    }

    [Fact]
    public void MissingFileReadsAsEmpty()
    {
        JournalSpillRead read = JournalSpill.Read(SpillPath);

        Assert.Empty(read.Records);
        Assert.False(read.TornTailIgnored);
        Assert.Equal(0, read.ValidLength);
    }

    [Fact]
    public void WriterRefusesToAppendOntoABrokenChain()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 13, count: 4);
        WriteSpill(records);

        // Change one hex digit in the hash field of line 2 (index 1). The line still parses, but the chain breaks there.
        string[] lines = File.ReadAllLines(SpillPath);
        string[] fields = lines[1].Split(' ');
        fields[1] = (fields[1][0] == '0' ? "1" : "0") + fields[1][1..];
        lines[1] = string.Join(' ', fields);
        File.WriteAllLines(SpillPath, lines);

        Assert.Throws<InvalidDataException>(() => JournalSpillWriter.Open(SpillPath));
    }

    private void WriteSpill(IEnumerable<JournalRecord> records)
    {
        using JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath);
        foreach (JournalRecord record in records)
        {
            writer.Append(record.At, record.Kind, record.Payload);
        }
    }
}
