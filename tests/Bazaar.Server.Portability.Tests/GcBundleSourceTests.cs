using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using static Bazaar.Server.Portability.Tests.GcFixtures;

namespace Bazaar.Server.Portability.Tests;

public sealed class GcBundleSourceTests : IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-gcbundle-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private BundleAssemblyOptions Options(string name) => new()
    {
        InstanceId = "inst-1",
        CreatedAt = _created,
        SigningKey = _key,
        OutputDirectory = Path.Combine(_root, name),
    };

    private static string GcPath(BundleAssemblyResult result, string file) =>
        Path.Combine(result.Directory, "gc", GcId.ToString("D"), file);

    [Fact]
    public async Task A_full_gc_bundle_assembles_and_verifies_end_to_end()
    {
        var (log, versions, mentions) = GcBundleSeed.Build();
        var source = new GcBundleSource(new GcJsonlExporter(log, versions, mentions: mentions), Owner, [GcId]);

        var result = await ExportBundleAssembler.AssembleAsync([source], Options("full"), TestContext.Current.CancellationToken);

        var bundle = await ExportBundleAssembler.ReadDirectoryAsync(result.Directory, TestContext.Current.CancellationToken);
        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.True(BundleSigner.Verify(bundle, _key.ExportSubjectPublicKeyInfo()));
        foreach (var file in new[] { "events.jsonl", "messages.jsonl", "versions.jsonl", "mentions.jsonl", "head.json" })
        {
            Assert.True(File.Exists(GcPath(result, file)), file);
        }

        Assert.Equal(3, File.ReadAllLines(GcPath(result, "mentions.jsonl")).Length);
        var head = JsonElement.Parse(File.ReadAllText(GcPath(result, "head.json")));
        Assert.Equal(5, head.GetProperty("headSeq").GetInt64());
        Assert.Equal(3, head.GetProperty("mentionCount").GetInt64());
        Assert.Single(source.Results);
        Assert.True(source.Results[0].Complete);
    }

    [Fact]
    public async Task Head_lists_the_mentions_file_with_its_hash()
    {
        var (log, versions, mentions) = GcBundleSeed.Build();
        var source = new GcBundleSource(new GcJsonlExporter(log, versions, mentions: mentions), Owner, [GcId]);

        var result = await ExportBundleAssembler.AssembleAsync([source], Options("head"), TestContext.Current.CancellationToken);

        var head = GcExportHead.TryParse(await File.ReadAllBytesAsync(GcPath(result, "head.json"), TestContext.Current.CancellationToken), out var problem);
        Assert.Null(problem);
        var listed = Assert.Single(head!.Files, f => f.Path.EndsWith("mentions.jsonl", StringComparison.Ordinal));
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(GcPath(result, "mentions.jsonl"), TestContext.Current.CancellationToken))).ToLowerInvariant();
        Assert.Equal(actual, listed.Sha256);
        Assert.Equal(3, head.MentionCount);
    }

    [Fact]
    public async Task Without_a_mention_reader_head_has_no_mention_fields_and_no_mentions_file()
    {
        var (log, versions, _) = GcBundleSeed.Build();
        var source = new GcBundleSource(new GcJsonlExporter(log, versions), Owner, [GcId]);

        var result = await ExportBundleAssembler.AssembleAsync([source], Options("nomentions"), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(GcPath(result, "mentions.jsonl")));
        Assert.False(JsonElement.Parse(File.ReadAllText(GcPath(result, "head.json"))).TryGetProperty("mentionCount", out _));
        Assert.Empty(ExportBundleWriter.Verify(await ExportBundleAssembler.ReadDirectoryAsync(result.Directory, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_gc_that_fails_its_self_check_aborts_the_bundle_and_leaves_nothing()
    {
        var (log, versions, mentions) = GcBundleSeed.Build();
        // A version row for an event that does not exist breaks the self-check (doc 44 section 6.2).
        versions.Rows.Add(Sealed(9, 3, 1, "orphan"));
        log.HeadOverride = 9;
        var source = new GcBundleSource(new GcJsonlExporter(log, versions, mentions: mentions), Owner, [GcId]);
        var options = Options("broken");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExportBundleAssembler.AssembleAsync([source], options, TestContext.Current.CancellationToken));

        Assert.Contains("incomplete", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task Scratch_files_are_removed_after_the_run()
    {
        var scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(scratch);
        var (log, versions, mentions) = GcBundleSeed.Build();
        var source = new GcBundleSource(new GcJsonlExporter(log, versions, mentions: mentions), Owner, [GcId], scratch);

        await ExportBundleAssembler.AssembleAsync([source], Options("clean"), TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    [Fact]
    public async Task The_self_check_finds_a_mentions_file_that_disagrees_with_head()
    {
        var (log, versions, mentions) = GcBundleSeed.Build();
        var store = new InMemoryExportFileStore();
        var exporter = new GcJsonlExporter(log, versions, mentions: mentions);
        await exporter.ExportAsync(GcId, Owner, store, CancellationToken.None);
        var path = GcJsonlFormat.PathFor(GcId, "mentions.jsonl");
        var lines = store.Lines(path);
        store.Replace(path, System.Text.Encoding.UTF8.GetBytes(lines[0] + "\n"));

        var check = await GcExportSelfCheck.VerifyAsync(store, GcId, CancellationToken.None);

        Assert.False(check.Passed);
        Assert.Contains(check.Problems, p => p.Contains("mentions", StringComparison.Ordinal));
    }
}
