using System.Globalization;
using System.Text;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;

namespace Bazaar.Wire.Bench;

/// <summary>
/// Synthetic, seeded corpora. Nothing here is real company data. The S6 corpus follows docs/spikes/S6.md: 10,000 messages
/// (60% short chat, 40% long prose with code blocks and non-ASCII text), a 200-message page and a 10,000-frame replay, fixed seed.
/// The Constellation snapshot is a synthetic graph, not a capture of a real vault.
/// </summary>
internal static class Corpus
{
    public const int Seed = 6;
    public const int MessageCount = 10_000;
    public const int PageSize = 200;
    public const int NodeCount = 5_000;
    public const int EdgeCount = 20_000;

    private static readonly string[] _words =
    [
        "the", "build", "is", "green", "again", "merge", "after", "review", "please", "queue", "agent", "wire", "frame", "codec", "vault", "graph",
        "ship", "it", "today", "tests", "pass", "on", "linux", "windows", "retry", "later", "owner", "approved", "kanban", "ready", "blocked", "by",
        "migration", "schema", "version", "lag", "stream", "replay", "page", "server", "client", "latency", "budget", "quota", "free", "tier", "model",
        "route", "cache", "index", "search", "note", "link", "memory", "thread", "decision", "spike", "result", "table", "draft", "fix", "bug", "cost",
    ];

    private static readonly string[] _nonAscii =
    [
        "déjà vu, naïve café", "日本語のメッセージです", "Привет, мир", "\U0001F642 \U0001F680 ✨", "über größer — straße",
    ];

    private static readonly string[] _code =
    [
        "```csharp\nvar page = await client.GetPageAsync(gcId, before: 120, limit: 50);\nforeach (var m in page.Items) { Console.WriteLine($\"{m.N}: {m.Text}\"); }\n```",
        "```sql\nSELECT n, ev, text FROM gc_message_version WHERE gc_id = $1 ORDER BY n DESC LIMIT 200;\n```",
        "```json\n{\"gc\": \"x\", \"limit\": 200, \"before\": null}\n```",
    ];

    /// <summary>10,000 gc.message payloads.</summary>
    public static List<WireGcMessage> Messages()
    {
        var rng = new Random(Seed);
        var gcs = Enumerable.Range(0, 20).Select(_ => NextGuid(rng)).ToArray();
        var authors = Enumerable.Range(0, 12).Select(_ => NextGuid(rng)).ToArray();
        var counters = new long[gcs.Length];
        var at = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var list = new List<WireGcMessage>(MessageCount);
        for (var i = 0; i < MessageCount; i++)
        {
            var g = rng.Next(gcs.Length);
            var n = ++counters[g];
            at = at.AddMilliseconds(rng.Next(200, 90_000));
            var text = rng.NextDouble() < 0.6 ? Short(rng) : Long(rng);
            List<Guid> mentions = rng.NextDouble() < 0.15 ? [.. Enumerable.Range(0, rng.Next(1, 4)).Select(_ => authors[rng.Next(authors.Length)])] : [];
            long? reply = rng.NextDouble() < 0.2 && n > 1 ? rng.NextInt64(1, n) : null;
            var edited = rng.NextDouble() < 0.05;
            list.Add(new WireGcMessage(gcs[g], n, edited ? n + rng.Next(1, 4) : n, NextGuid(rng), authors[rng.Next(authors.Length)], text, at, edited, mentions, reply));
        }

        return list;
    }

    /// <summary>A synthetic Constellation snapshot (doc 19 shapes): 5,000 nodes and 20,000 edges, fixed seed.</summary>
    public static GraphSnapshot Snapshot()
    {
        var rng = new Random(Seed + 1);
        var nodes = new List<GraphNode>(NodeCount);
        for (var i = 0; i < NodeCount; i++)
        {
            var roll = rng.NextDouble();
            GraphNodeKind kind;
            string prefix;
            if (roll < 0.1)
            {
                kind = GraphNodeKind.Phase;
                prefix = "phase";
            }
            else if (roll < 0.4)
            {
                kind = GraphNodeKind.Objective;
                prefix = "objective";
            }
            else
            {
                kind = GraphNodeKind.Vault;
                prefix = "vault/" + _words[rng.Next(_words.Length)] + "/" + _words[rng.Next(_words.Length)];
            }

            nodes.Add(new GraphNode(prefix + "-" + i.ToString("D5", CultureInfo.InvariantCulture) + (kind == GraphNodeKind.Vault ? ".md" : string.Empty), kind));
        }

        var edges = new List<GraphEdge>(EdgeCount);
        var kinds = new[] { GraphEdgeKind.DependsOn, GraphEdgeKind.PhaseMembership, GraphEdgeKind.WikiLink, GraphEdgeKind.WikiLink, GraphEdgeKind.WikiLink };
        for (var i = 0; i < EdgeCount; i++)
        {
            edges.Add(new GraphEdge(nodes[rng.Next(NodeCount)].Id, nodes[rng.Next(NodeCount)].Id, kinds[rng.Next(kinds.Length)]));
        }

        return new GraphSnapshot(1, nodes, edges);
    }

    private static string Short(Random rng)
    {
        var sb = new StringBuilder();
        var count = rng.Next(2, 18);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(_words[rng.Next(_words.Length)]);
        }

        if (rng.NextDouble() < 0.1) sb.Append(' ').Append(_nonAscii[rng.Next(_nonAscii.Length)]);
        return sb.ToString();
    }

    private static string Long(Random rng)
    {
        var sb = new StringBuilder();
        var sentences = rng.Next(4, 14);
        for (var s = 0; s < sentences; s++)
        {
            var count = rng.Next(8, 24);
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(_words[rng.Next(_words.Length)]);
            }

            sb.Append(". ");
        }

        if (rng.NextDouble() < 0.35) sb.Append('\n').Append(_code[rng.Next(_code.Length)]);
        if (rng.NextDouble() < 0.35) sb.Append('\n').Append(_nonAscii[rng.Next(_nonAscii.Length)]);
        return sb.ToString().TrimEnd();
    }

    private static Guid NextGuid(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }
}
