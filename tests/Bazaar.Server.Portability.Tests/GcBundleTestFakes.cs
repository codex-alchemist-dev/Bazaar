using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;

namespace Bazaar.Server.Portability.Tests;

/// <summary>An in-memory mention table that pages by (seq, target) like the real reader. Shared with the verifier tests by link.</summary>
internal sealed class FakeBundleMentions : IGcMentionSource
{
    public List<GcMentionRecord> Rows { get; } = [];

    public async IAsyncEnumerable<GcMentionRecord> ReadAsync(
        Guid gcId, long afterSeq, Guid afterTarget, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var page = Rows
            .OrderBy(r => r.Seq).ThenBy(r => r.TargetPrincipalId.ToString("D"), StringComparer.Ordinal)
            .Where(r => r.Seq > afterSeq || (r.Seq == afterSeq && string.CompareOrdinal(r.TargetPrincipalId.ToString("D"), afterTarget.ToString("D")) > 0))
            .Take(limit);
        foreach (var row in page)
        {
            yield return row;
        }
    }
}

/// <summary>A GC with messages, an edit, a tombstone and mentions, the same one for every end-to-end bundle test.</summary>
internal static class GcBundleSeed
{
    public static (FakeGcLog Log, FakeVersionSource Versions, FakeBundleMentions Mentions) Build()
    {
        var log = new FakeGcLog();
        log.Events.Add(GcFixtures.Event(1, GcEventKinds.System));
        log.Events.Add(GcFixtures.MessageEvent(2, 1, 1));
        log.Events.Add(GcFixtures.MessageEvent(3, 2, 1));
        log.Events.Add(GcFixtures.MessageEvent(4, 1, 2));
        log.Events.Add(GcFixtures.MessageEvent(5, 2, 2));
        var versions = new FakeVersionSource();
        versions.Rows.Add(GcFixtures.Sealed(2, 1, 1, "hello @a"));
        versions.Rows.Add(GcFixtures.Sealed(3, 2, 1, "second message"));
        versions.Rows.Add(GcFixtures.Sealed(4, 1, 2, "hello @a and @b"));
        versions.Rows.Add(GcFixtures.Tombstone(5, 2, 2));
        var mentions = new FakeBundleMentions();
        mentions.Rows.Add(new GcMentionRecord(2, GcFixtures.Id(70)));
        mentions.Rows.Add(new GcMentionRecord(4, GcFixtures.Id(70)));
        mentions.Rows.Add(new GcMentionRecord(4, GcFixtures.Id(71)));
        return (log, versions, mentions);
    }
}
