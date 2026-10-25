namespace Spike.S6.Wire;

/// <summary>Deterministic synthetic chat corpus (no real data): short chat, long prose, code blocks, non-ASCII.</summary>
public static class Corpus
{
    private static readonly string[] Short =
    [
        "ok", "on it", "merged, CI is green", "can you review the migration before I start G2?", "👍",
        "@charles the Postgres job is red on the partition test", "lgtm", "thanks, picking up the next card",
    ];

    private static readonly string[] Long =
    [
        "I read the gateway doc again and the fallback chain never reaches a model outside the agent's whitelist, " +
        "but the usage record still needs the source label so the cost chip can show Anthropic key versus FreeLLMAPI. " +
        "I will add the label to gw_usage in the adapter and leave the policy check where it is.",
        "```csharp\npublic async Task<long> AppendAsync(Guid gcId, ReadOnlyMemory<byte> body, CancellationToken ct)\n{\n" +
        "    await using var tx = await _db.BeginTransactionAsync(ct);\n    var n = await NextSeqAsync(tx, gcId, ct);\n" +
        "    await InsertAsync(tx, gcId, n, body, ct);\n    await NotifyAsync(tx, gcId, n, ct);\n    await tx.CommitAsync(ct);\n    return n;\n}\n```",
        "Résumé de la réunion : 日本語のテストも含める。Привет, мир. Ünïcödé everywhere, and an emoji 🚀 or two 🎉.",
    ];

    public static GcMessage[] Messages(int count, string gcId = "gc-0001", long startN = 1, int seed = 42)
    {
        var rnd = new Random(seed);
        var t0 = new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero);
        var items = new GcMessage[count];
        for (int i = 0; i < count; i++)
        {
            double r = rnd.NextDouble();
            string text = r < 0.6 ? Short[rnd.Next(Short.Length)] : Long[rnd.Next(Long.Length)];
            string[] mentions = rnd.NextDouble() < 0.2 ? ["agent-charles"] : [];
            items[i] = new GcMessage(gcId, startN + i, startN + i, $"msg-{seed}-{i:D6}",
                $"agent-{rnd.Next(1, 9):D2}", text, t0.AddSeconds(i * 3).AddMilliseconds(0), rnd.NextDouble() < 0.05, mentions);
        }
        return items;
    }
}
