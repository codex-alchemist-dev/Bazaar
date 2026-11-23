using System.Security.Cryptography;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Tests.Journal;

namespace Bazaar.Kodo.Tests.Fuzz;

/// <summary>
/// Fuzzing and mutation tests for the journal (doc 46 sections 6.5, 7.5 and 17.4): the canonical decoder, the spill reader, the hash
/// chain and idempotent replay. One changed bit in a record, a link or a stored line must be caught or must change nothing.
/// </summary>
public sealed class JournalFuzzTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kodo-journal-fuzz-" + Guid.NewGuid().ToString("N"));

    public JournalFuzzTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    public void CanonicalDecoderRejectsOnlyWithInvalidDataException(int seed)
    {
        List<JournalRecord> chain = JournalSamples.Chain(seed, 6);
        byte[][] valid = [.. chain.Select(JournalCodec.Encode)];
        byte[] previous = JournalChain.GenesisHash();
        byte[] hash = new byte[JournalChain.HashLength];

        FuzzKit.Run(
            "JournalCodec.Decode",
            seed,
            1500,
            (random, i) => i % 3 == 0 ? FuzzKit.RandomBytes(random, 80) : FuzzKit.Mutate(random, valid[random.Next(valid.Length)]),
            input =>
            {
                JournalRecord record = JournalCodec.Decode(input, previous, hash);

                // A decoded record re-encodes to the bytes it came from, unless the kind held invalid UTF-8 or was empty.
                if (record.Kind.Length > 0 && !record.Kind.Contains('�', StringComparison.Ordinal))
                {
                    Assert.Equal(input, JournalCodec.Encode(record));
                }
            },
            typeof(InvalidDataException));
    }

    [Theory]
    [InlineData(411)]
    [InlineData(412)]
    [InlineData(413)]
    public void SpillReaderRejectsOnlyWithInvalidDataExceptionAndNeverTouchesTheFile(int seed)
    {
        List<JournalRecord> chain = JournalSamples.Chain(seed, 5);
        byte[] valid = [.. chain.SelectMany(JournalSpill.FormatLine)];
        string path = Path.Combine(_directory, $"spill-{seed}.log");

        FuzzKit.Run(
            "JournalSpill.Read",
            seed,
            250,
            (random, i) => i % 5 == 0 ? FuzzKit.RandomBytes(random, 200) : FuzzKit.Mutate(random, valid),
            input =>
            {
                File.WriteAllBytes(path, input);
                byte[] before = SHA256.HashData(input);

                try
                {
                    JournalSpillRead read = JournalSpill.Read(path);
                    Assert.InRange(read.ValidLength, 0, input.Length);
                    Assert.Equal(!read.TornTailIgnored, read.ValidLength == input.Length);
                    Assert.True(input.Length == 0 || read.TornTailIgnored || input[^1] == (byte)'\n');
                }
                finally
                {
                    // A read, accepted or rejected, never changes the file.
                    Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
                }
            },
            typeof(InvalidDataException));
    }

    [Fact]
    public void OneChangedBitInAnyFieldOfAnyRecordBreaksTheChainAtThatRecord()
    {
        List<JournalRecord> chain = JournalSamples.Chain(seed: 421, count: 8);
        Assert.True(JournalChain.Verify(chain).IsValid);

        for (int index = 0; index < chain.Count; index++)
        {
            JournalRecord original = chain[index];
            List<(string Field, JournalRecord Changed)> changes = [];

            for (int bit = 0; bit < 64; bit++)
            {
                changes.Add(($"sequence bit {bit}", original with { Sequence = original.Sequence ^ (1L << bit) }));
                long ticks = original.At.UtcTicks ^ (1L << bit);
                if (ticks is >= 0 and <= 3_155_378_975_999_999_999L)
                {
                    changes.Add(($"ticks bit {bit}", original with { At = new DateTimeOffset(ticks, TimeSpan.Zero) }));
                }
            }

            for (int at = 0; at < original.Payload.Length; at++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    byte[] payload = [.. original.Payload];
                    payload[at] ^= (byte)(1 << bit);
                    changes.Add(($"payload byte {at} bit {bit}", original with { Payload = payload }));
                }
            }

            for (int at = 0; at < JournalChain.HashLength; at++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    byte[] previous = [.. original.PreviousHash];
                    previous[at] ^= (byte)(1 << bit);
                    changes.Add(($"previous hash byte {at} bit {bit}", original with { PreviousHash = previous }));
                    byte[] hash = [.. original.Hash];
                    hash[at] ^= (byte)(1 << bit);
                    changes.Add(($"hash byte {at} bit {bit}", original with { Hash = hash }));
                }
            }

            string kind = original.Kind;
            changes.Add(("kind first char", original with { Kind = ((char)(kind[0] ^ 1)) + kind[1..] }));
            changes.Add(("kind longer", original with { Kind = kind + "x" }));

            foreach ((string field, JournalRecord changed) in changes)
            {
                List<JournalRecord> copy = [.. chain];
                copy[index] = changed;

                JournalVerification result = JournalChain.Verify(copy);
                string brokenAt = result.FirstBrokenIndex is { } broken ? $"{broken}" : "none";

                Assert.True(result.FirstBrokenIndex == index, $"record {index}, {field}: first broken index was {brokenAt}");
            }
        }
    }

    [Theory]
    [InlineData(431)]
    [InlineData(432)]
    [InlineData(433)]
    public void DroppingSwappingOrRepeatingRecordsIsAlwaysDetected(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        List<JournalRecord> chain = JournalSamples.Chain(seed, 12);

        for (int round = 0; round < 60; round++)
        {
            List<JournalRecord> copy = [.. chain];
            int a = random.Next(1, copy.Count - 1);
            int expected;
            string what;
            switch (random.Next(3))
            {
                case 0:
                    copy.RemoveAt(a);
                    expected = a;
                    what = $"drop {a}";
                    break;
                case 1:
                    (copy[a], copy[a + 1]) = (copy[a + 1], copy[a]);
                    expected = a;
                    what = $"swap {a},{a + 1}";
                    break;
                default:
                    copy.Insert(a, copy[a - 1]);
                    expected = a;
                    what = $"repeat {a - 1} at {a}";
                    break;
            }

            Assert.True(JournalChain.Verify(copy).FirstBrokenIndex == expected, $"seed={seed} round={round}: {what}");
        }
    }

    [Theory]
    [InlineData(441)]
    [InlineData(442)]
    [InlineData(443)]
    public void OneBitFlipInAStoredSpillIsRejectedDetectedOrChangesNothing(int seed)
    {
        List<JournalRecord> chain = JournalSamples.Chain(seed, 6);
        byte[] valid = [.. chain.SelectMany(JournalSpill.FormatLine)];
        string path = Path.Combine(_directory, $"flip-{seed}.log");
        Random random = FuzzKit.Rng(seed);

        for (int index = 0; index < valid.Length; index++)
        {
            byte[] mutated = [.. valid];
            int bit = random.Next(8);
            mutated[index] ^= (byte)(1 << bit);
            File.WriteAllBytes(path, mutated);
            string where = $"seed={seed} byte={index} bit={bit}";

            JournalSpillRead read;
            try
            {
                read = JournalSpill.Read(path);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!JournalChain.Verify(read.Records).IsValid)
            {
                continue;
            }

            // The chain still verifies, so what was read must be the original records (or a prefix, which the torn-tail flag reports).
            Assert.True(read.Records.Count <= chain.Count, $"{where}: records appeared");
            if (read.Records.Count < chain.Count)
            {
                Assert.True(read.TornTailIgnored, $"{where}: records vanished and no torn tail was reported");
            }

            for (int record = 0; record < read.Records.Count; record++)
            {
                Assert.Equal(chain[record].Hash, read.Records[record].Hash);
                Assert.Equal(JournalCodec.Encode(chain[record]), JournalCodec.Encode(read.Records[record]));
            }
        }
    }

    [Theory]
    [InlineData(451)]
    [InlineData(452)]
    [InlineData(453)]
    public void ReplayAppliesEachHashOnceInOrderAndTwiceChangesNothing(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        List<JournalRecord> pool = JournalSamples.Chain(seed, 20);

        for (int round = 0; round < 80; round++)
        {
            List<JournalRecord> input = [];
            int length = random.Next(0, 40);
            for (int i = 0; i < length; i++)
            {
                input.Add(pool[random.Next(pool.Count)]);
            }

            HashSet<string> applied = new(StringComparer.Ordinal);
            foreach (JournalRecord seen in pool.Where(_ => random.Next(4) == 0))
            {
                applied.Add(JournalChain.KeyOf(seen));
            }

            HashSet<string> before = [.. applied];
            string where = $"seed={seed} round={round}";

            IReadOnlyList<JournalRecord> first = JournalReplay.Apply(input, applied);

            string[] expected = [.. input.Select(JournalChain.KeyOf).Where(key => !before.Contains(key)).Distinct()];
            Assert.Equal(expected, first.Select(JournalChain.KeyOf));
            Assert.True(applied.SetEquals(before.Union(input.Select(JournalChain.KeyOf))), $"{where}: set is not the union");

            HashSet<string> afterFirst = [.. applied];
            Assert.Empty(JournalReplay.Apply(input, applied));
            Assert.True(applied.SetEquals(afterFirst), $"{where}: a second replay changed the set");
        }
    }
}
