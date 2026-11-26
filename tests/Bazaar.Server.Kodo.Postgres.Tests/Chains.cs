using System.Text;
using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo.Postgres.Tests;

internal static class Chains
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A valid chain of <paramref name="count"/> records starting at <paramref name="firstSequence"/>, linked to <paramref name="previous"/> (genesis when null).</summary>
    public static List<JournalRecord> Build(int count, long firstSequence = 0, byte[]? previous = null, string kind = "observation", Func<int, string>? summary = null)
    {
        List<JournalRecord> records = [];
        byte[] link = previous ?? JournalChain.GenesisHash();
        for (int i = 0; i < count; i++)
        {
            JournalRecord record = JournalChain.Create(
                firstSequence + i,
                T0.AddSeconds(firstSequence + i).AddTicks(7),
                kind,
                Encoding.UTF8.GetBytes(summary?.Invoke(i) ?? $"record {firstSequence + i}"),
                link);
            records.Add(record);
            link = record.Hash;
        }

        return records;
    }
}
