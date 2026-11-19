using System.Collections.Concurrent;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Tests.Journal;

namespace Bazaar.Kodo.Tests.Chaos;

/// <summary>
/// Fault harness for the journal (doc 46 section 17.1 and 17.4): kill at every byte boundary, mutate every byte
/// and every bit, fuzz the decoder, disk full, concurrent and competing writers. Deterministic seeds, no waits.
/// </summary>
public sealed class JournalChaosTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kodo-chaos-" + Guid.NewGuid().ToString("N"));

    public JournalChaosTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SpillPath => Path.Combine(_directory, "spill.log");

    private static byte[] Build(List<JournalRecord> records)
    {
        using MemoryStream stream = new();
        foreach (JournalRecord record in records)
        {
            stream.Write(JournalSpill.FormatLine(record));
        }

        return stream.ToArray();
    }

    private static bool SameRecords(IReadOnlyList<JournalRecord> read, List<JournalRecord> original) =>
        read.Count == original.Count
        && read.Zip(original).All(pair => JournalCodec.Encode(pair.First).AsSpan().SequenceEqual(JournalCodec.Encode(pair.Second))
            && pair.First.Hash.AsSpan().SequenceEqual(pair.Second.Hash)
            && pair.First.PreviousHash.AsSpan().SequenceEqual(pair.Second.PreviousHash));

    [Fact]
    public void KillAtEveryByteOffsetRecoversExactlyTheFullyWrittenPrefix()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 11, count: 8);
        byte[] image = Build(records);
        List<int> boundaries = [];
        int running = 0;
        foreach (JournalRecord record in records)
        {
            running += JournalSpill.FormatLine(record).Length;
            boundaries.Add(running);
        }

        for (int cut = 0; cut <= image.Length; cut++)
        {
            File.WriteAllBytes(SpillPath, image.AsSpan(0, cut).ToArray());
            int expected = boundaries.Count(b => b <= cut);

            JournalSpillRead read = JournalSpill.Read(SpillPath);
            Assert.Equal(expected, read.Records.Count);
            Assert.True(JournalChain.Verify(read.Records).IsValid);
            Assert.Equal(expected == 0 ? 0 : boundaries[expected - 1], read.ValidLength);
            Assert.Equal(cut > read.ValidLength, read.TornTailIgnored);

            // Reopening for append repairs the tail, keeps every full record and keeps chaining.
            using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
            {
                Assert.Equal(expected, writer.NextSequence);
                writer.Append(DateTimeOffset.UnixEpoch, "after.crash", [1, 2, 3]);
            }

            JournalSpillRead after = JournalSpill.Read(SpillPath);
            Assert.False(after.TornTailIgnored);
            Assert.Equal(expected + 1, after.Records.Count);
            Assert.True(JournalChain.Verify(after.Records).IsValid);
        }
    }

    [Fact]
    public void EverySingleBitFlipIsDetectedOrIsAnExactNoOp()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 12, count: 4);
        byte[] image = Build(records);
        List<string> undetected = [];

        for (int offset = 0; offset < image.Length; offset++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] mutated = [.. image];
                mutated[offset] ^= (byte)(1 << bit);
                File.WriteAllBytes(SpillPath, mutated);

                try
                {
                    JournalSpillRead read = JournalSpill.Read(SpillPath);
                    bool same = read.TornTailIgnored || SameRecords(read.Records, records);
                    if (JournalChain.Verify(read.Records).IsValid && !same)
                    {
                        undetected.Add($"{offset}:{bit}");
                    }
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        Assert.Empty(undetected);
    }

    [Fact]
    public void EverySingleByteReplacementIsDetectedOrIsAnExactNoOp()
    {
        List<JournalRecord> records = JournalSamples.Chain(seed: 13, count: 2);
        byte[] image = Build(records);
        List<string> undetected = [];

        for (int offset = 0; offset < image.Length; offset++)
        {
            int original = image[offset];
            foreach (int value in new[] { 0, 1, 0x0A, 0x20, 0x41, 0x7F, 0x80, 0xFF, original ^ 1, original ^ 0x20, original + 1, original - 1 })
            {
                if (value is < 0 or > 255 || original == value)
                {
                    continue;
                }

                byte[] mutated = [.. image];
                mutated[offset] = (byte)value;
                File.WriteAllBytes(SpillPath, mutated);
                try
                {
                    JournalSpillRead read = JournalSpill.Read(SpillPath);
                    bool same = read.TornTailIgnored || SameRecords(read.Records, records);
                    if (JournalChain.Verify(read.Records).IsValid && !same)
                    {
                        undetected.Add($"{offset}={value}");
                    }
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        Assert.Empty(undetected);
    }

    [Fact]
    public void DecoderFuzzOnlyEverThrowsInvalidData()
    {
#pragma warning disable CA5394 // fixed seed, not security material
        Random random = new(4242);
#pragma warning restore CA5394
        List<JournalRecord> seeds = JournalSamples.Chain(seed: 14, count: 6);
        for (int round = 0; round < 20_000; round++)
        {
            byte[] canonical = JournalCodec.Encode(seeds[random.Next(seeds.Count)]);
            switch (random.Next(5))
            {
                case 0:
                    random.NextBytes(canonical);
                    break;
                case 1:
                    canonical = canonical.AsSpan(0, random.Next(canonical.Length + 1)).ToArray();
                    break;
                case 2:
                    // Structure-aware: hostile kind-length and payload-length fields.
                    random.NextBytes(canonical.AsSpan(17, 2));
                    random.NextBytes(canonical.AsSpan(19, 4));
                    break;
                case 3:
                    random.NextBytes(canonical.AsSpan(1, 16));
                    break;
                default:
                    canonical[0] = (byte)random.Next(256);
                    break;
            }

            try
            {
                _ = JournalCodec.Decode(canonical, JournalChain.GenesisHash(), new byte[32]);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void SpillReaderFuzzOnlyEverThrowsInvalidData()
    {
#pragma warning disable CA5394
        Random random = new(777);
#pragma warning restore CA5394
        byte[] valid = Build(JournalSamples.Chain(seed: 15, count: 5));
        for (int round = 0; round < 3_000; round++)
        {
            byte[] bytes = [.. valid];
            int edits = random.Next(1, 6);
            for (int edit = 0; edit < edits; edit++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            if (random.Next(4) == 0)
            {
                bytes = bytes.AsSpan(0, random.Next(bytes.Length)).ToArray();
            }

            File.WriteAllBytes(SpillPath, bytes);
            try
            {
                JournalSpillRead read = JournalSpill.Read(SpillPath);
                _ = JournalChain.Verify(read.Records);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void ForgedFieldsAreNeverAcceptedByVerify()
    {
        JournalRecord good = JournalChain.Create(0, DateTimeOffset.UnixEpoch, "k", [1], JournalChain.GenesisHash());
        Assert.True(JournalChain.Verify([good]).IsValid);

        Assert.False(JournalChain.Verify([good with { Sequence = -1 }]).IsValid);
        Assert.False(JournalChain.Verify([good with { Sequence = 1 }]).IsValid);
        Assert.False(JournalChain.Verify([good with { Kind = string.Empty }]).IsValid);
        Assert.False(JournalChain.Verify([good with { Hash = [] }]).IsValid);
        Assert.False(JournalChain.Verify([good with { PreviousHash = [] }]).IsValid);
        Assert.False(JournalChain.Verify([good, good]).IsValid);
    }

    [Fact]
    public void ConcurrentAppendersOnOneWriterKeepAValidChain()
    {
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
        {
            ConcurrentBag<Exception> errors = [];
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    writer.Append(DateTimeOffset.UnixEpoch, "c", BitConverter.GetBytes(i));
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            });
            Assert.Empty(errors);
            Assert.Equal(400, writer.NextSequence);
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);
        Assert.Equal(400, read.Records.Count);
        Assert.True(JournalChain.Verify(read.Records).IsValid);
    }

    [Fact]
    public void SecondWriterOnTheSameFileIsRefusedWithIoException()
    {
        using JournalSpillWriter first = JournalSpillWriter.Open(SpillPath);
        first.Append(DateTimeOffset.UnixEpoch, "a", []);
        Assert.Throws<IOException>(() => JournalSpillWriter.Open(SpillPath));
        first.Append(DateTimeOffset.UnixEpoch, "b", []);
        first.Dispose();
        Assert.True(JournalChain.Verify(JournalSpill.Read(SpillPath).Records).IsValid);
    }

    [Fact]
    public void DiskFullMidAppendDoesNotPoisonTheFile()
    {
        for (int budget = 0; budget < 120; budget += 7)
        {
            string path = Path.Combine(_directory, $"full-{budget}.log");
            FaultyStream? faulty = null;
            using (JournalSpillWriter writer = JournalSpillWriter.Open(path, file => faulty = new FaultyStream(file)))
            {
                writer.Append(DateTimeOffset.UnixEpoch, "first", [1]);
                faulty!.Remaining = budget;
                try
                {
                    writer.Append(DateTimeOffset.UnixEpoch, "second", new byte[40]);
                }
                catch (IOException)
                {
                }

                // Space returns. The writer must still produce a valid chain, whether or not the failing append landed.
                faulty.Remaining = long.MaxValue;
                writer.Append(DateTimeOffset.UnixEpoch, "third", [3]);
            }

            JournalSpillRead read = JournalSpill.Read(path);
            Assert.False(read.TornTailIgnored);
            Assert.True(JournalChain.Verify(read.Records).IsValid, $"chain broken at budget {budget}");
            Assert.Contains(read.Records, r => r.Kind == "first");
            Assert.Contains(read.Records, r => r.Kind == "third");
            using JournalSpillWriter reopened = JournalSpillWriter.Open(path);
        }
    }

    private sealed class FaultyStream(Stream inner) : Stream
    {
        public long Remaining { get; set; } = long.MaxValue;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > Remaining)
            {
                int part = (int)Remaining;
                inner.Write(buffer[..part]);
                Remaining = 0;
                throw new IOException("No space left on device.");
            }

            Remaining -= buffer.Length;
            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
