using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Tests.Journal;

/// <summary>Deterministic chain builder for tests: the same seed always gives the same records.</summary>
internal static class JournalSamples
{
    private static readonly string[] Kinds = ["state.change", "observation", "kodo.self.spill", "鼓動.observation"];

    public static List<JournalRecord> Chain(int seed, int count)
    {
        // Fixed seeds, not security material; System.Random is deliberate here.
#pragma warning disable CA5394
        Random random = new(seed);
#pragma warning restore CA5394
        List<JournalRecord> records = [];
        byte[] previous = JournalChain.GenesisHash();
        DateTimeOffset at = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < count; index++)
        {
            byte[] payload = new byte[random.Next(0, 48)];
            random.NextBytes(payload);
            at = at.AddMilliseconds(random.Next(1, 5000));
            JournalRecord record = JournalChain.Create(index, at, Kinds[random.Next(Kinds.Length)], payload, previous);
            records.Add(record);
            previous = record.Hash;
        }

        return records;
    }

    /// <summary>Returns a copy of <paramref name="source"/> with one byte flipped.</summary>
    public static byte[] FlipByte(byte[] source, int offset)
    {
        byte[] copy = [.. source];
        copy[offset] ^= 0xFF;
        return copy;
    }
}
