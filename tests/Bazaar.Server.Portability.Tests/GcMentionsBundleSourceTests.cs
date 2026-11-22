using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Portability.Tests;

public sealed class GcMentionsBundleSourceTests : IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-mentions-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>An in-memory mention table that pages by (seq, target) like the real reader.</summary>
    private sealed class FakeMentions : IGcMentionSource
    {
        public Dictionary<Guid, List<GcMentionRecord>> Rows { get; } = [];

        public bool Denied { get; set; }

        public bool Disordered { get; set; }

        public int PagesServed { get; private set; }

        public async IAsyncEnumerable<GcMentionRecord> ReadAsync(
            Guid gcId, long afterSeq, Guid afterTarget, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            PagesServed++;
            if (Denied)
            {
                throw new GcAccessDeniedException(gcId);
            }

            var all = Rows.TryGetValue(gcId, out var rows) ? rows : [];
            var ordered = all.OrderBy(r => r.Seq).ThenBy(r => r.TargetPrincipalId.ToString("D"), StringComparer.Ordinal).ToList();
            var page = ordered
                .Where(r => r.Seq > afterSeq || (r.Seq == afterSeq && string.CompareOrdinal(r.TargetPrincipalId.ToString("D"), afterTarget.ToString("D")) > 0))
                .Take(limit)
                .ToList();
            if (Disordered)
            {
                page.Reverse();
            }

            foreach (var row in page)
            {
                yield return row;
            }
        }
    }

    private static Guid Gc(long n) => GcFixtures.Id(500 + n);

    private static FakeGcLog Log(long head) => new() { HeadOverride = head };

    private BundleAssemblyOptions Options(string name) => new()
    {
        InstanceId = "inst-1",
        CreatedAt = _created,
        SigningKey = _key,
        OutputDirectory = Path.Combine(_root, name),
    };

    private static Task<BundleAssemblyResult> Run(IBundleSource source, BundleAssemblyOptions options) =>
        ExportBundleAssembler.AssembleAsync([source], options, TestContext.Current.CancellationToken);

    private static string[] Lines(BundleAssemblyResult result, Guid gc) =>
        File.ReadAllLines(Path.Combine(result.Directory, "gc", gc.ToString("D"), "mentions.jsonl"));

    [Fact]
    public async Task Writes_one_sealed_line_per_mention_in_seq_order()
    {
        var mentions = new FakeMentions();
        mentions.Rows[Gc(1)] = [new(3, GcFixtures.Id(20)), new(1, GcFixtures.Id(21)), new(3, GcFixtures.Id(10))];
        var source = new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]);

        var result = await Run(source, Options("a"));

        var lines = Lines(result, Gc(1));
        Assert.Equal(3, lines.Length);
        var seqs = lines.Select(l => JsonElement.Parse(l).GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal([1L, 3L, 3L], seqs);
        Assert.Equal(GcFixtures.Id(10).ToString("D"), JsonElement.Parse(lines[1]).GetProperty("targetPrincipalId").GetString());
        Assert.Equal(3, source.Counts[Gc(1)]);
    }

    [Fact]
    public async Task Lines_chain_with_the_gc_jsonl_hash_rule()
    {
        var mentions = new FakeMentions();
        mentions.Rows[Gc(1)] = [new(1, GcFixtures.Id(20)), new(2, GcFixtures.Id(21)), new(2, GcFixtures.Id(22))];
        var source = new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]);

        var result = await Run(source, Options("a"));

        var prev = GcJsonlFormat.GenesisHash;
        foreach (var line in Lines(result, Gc(1)))
        {
            Assert.Null(GcJsonlFormat.CheckLine(line, prev, out var document, out var hash));
            document!.Dispose();
            prev = hash;
        }
    }

    [Fact]
    public async Task Paging_does_not_lose_mentions_that_share_a_seq()
    {
        var mentions = new FakeMentions();
        // Five mentions on the head seq with a page of two: a page ends between rows of one seq twice.
        mentions.Rows[Gc(1)] = [.. Enumerable.Range(1, 5).Select(i => new GcMentionRecord(5, GcFixtures.Id(30 + i)))];
        var source = new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)], pageSize: 2);

        var result = await Run(source, Options("a"));

        Assert.Equal(5, Lines(result, Gc(1)).Length);
        Assert.True(mentions.PagesServed >= 3);
    }

    [Fact]
    public async Task Mentions_past_the_head_are_left_out()
    {
        var mentions = new FakeMentions();
        mentions.Rows[Gc(1)] = [new(2, GcFixtures.Id(20)), new(4, GcFixtures.Id(21)), new(9, GcFixtures.Id(22))];
        var source = new GcMentionsBundleSource(Log(4), mentions, GcFixtures.Owner, [Gc(1)]);

        var result = await Run(source, Options("a"));

        Assert.Equal(2, Lines(result, Gc(1)).Length);
    }

    [Fact]
    public async Task A_gc_with_no_mentions_gets_an_empty_file()
    {
        var source = new GcMentionsBundleSource(Log(5), new FakeMentions(), GcFixtures.Owner, [Gc(1)]);

        var result = await Run(source, Options("a"));

        var entry = result.Files.Single(f => f.Path == "gc/" + Gc(1).ToString("D") + "/mentions.jsonl");
        Assert.Equal(0, entry.Bytes);
        Assert.Equal(0, source.Counts[Gc(1)]);
    }

    [Fact]
    public async Task Each_gc_gets_its_own_file_and_duplicate_ids_are_written_once()
    {
        var mentions = new FakeMentions();
        mentions.Rows[Gc(1)] = [new(1, GcFixtures.Id(20))];
        mentions.Rows[Gc(2)] = [new(1, GcFixtures.Id(21)), new(2, GcFixtures.Id(21))];
        var source = new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(2), Gc(1), Gc(2)]);

        var result = await Run(source, Options("a"));

        Assert.Single(Lines(result, Gc(1)));
        Assert.Equal(2, Lines(result, Gc(2)).Length);
        Assert.Equal(2, result.Files.Count(f => f.Path.EndsWith("/mentions.jsonl", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Output_is_deterministic()
    {
        var mentions = new FakeMentions();
        mentions.Rows[Gc(1)] = [new(1, GcFixtures.Id(20)), new(2, GcFixtures.Id(21))];

        var one = await Run(new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]), Options("one"));
        var two = await Run(new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]), Options("two"));

        Assert.Equal(one.Manifest, two.Manifest);
    }

    [Fact]
    public async Task Bundle_with_mentions_verifies_and_is_signed()
    {
        var mentions = new FakeMentions();
        mentions.Rows[Gc(1)] = [new(1, GcFixtures.Id(20))];
        var result = await Run(new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]), Options("a"));
        var bundle = await ExportBundleAssembler.ReadDirectoryAsync(result.Directory, TestContext.Current.CancellationToken);

        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.True(BundleSigner.Verify(bundle, _key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task A_viewer_without_access_aborts_the_bundle_and_leaves_nothing()
    {
        var mentions = new FakeMentions { Denied = true };
        var options = Options("a");
        var source = new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]);

        await Assert.ThrowsAsync<GcAccessDeniedException>(() => Run(source, options));

        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task A_reader_that_returns_rows_out_of_order_aborts_the_bundle()
    {
        var mentions = new FakeMentions { Disordered = true };
        mentions.Rows[Gc(1)] = [new(1, GcFixtures.Id(20)), new(2, GcFixtures.Id(21))];
        var options = Options("a");
        var source = new GcMentionsBundleSource(Log(5), mentions, GcFixtures.Owner, [Gc(1)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(source, options));

        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public void Constructor_rejects_a_page_size_outside_the_reader_limit()
    {
        var mentions = new FakeMentions();

        Assert.Throws<ArgumentOutOfRangeException>(() => new GcMentionsBundleSource(Log(1), mentions, GcFixtures.Owner, [], pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GcMentionsBundleSource(Log(1), mentions, GcFixtures.Owner, [], pageSize: IGcLogReader.MaxLimit + 1));
    }

    [Fact]
    public void Source_id_is_stable()
    {
        var source = new GcMentionsBundleSource(Log(1), new FakeMentions(), GcFixtures.Owner, []);

        Assert.Equal("gc.mentions.v1", source.Id);
    }
}
