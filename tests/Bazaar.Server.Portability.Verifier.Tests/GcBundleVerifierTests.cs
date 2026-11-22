using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Portability.Tests;

namespace Bazaar.Server.Portability.Verifier.Tests;

/// <summary>
/// Doc 44 section 6.1: the verifier reads a GC bundle exactly as <see cref="GcJsonlExporter"/> and <see cref="GcBundleSource"/> write it
/// (head.json with <c>headSeq</c> and flat counts, chain fields <c>prevHash</c> and <c>hash</c>), including the mention count.
/// </summary>
public sealed class GcBundleVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-gcverify-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<string> AssembleAsync(bool withMentions = true)
    {
        var (log, versions, mentions) = GcBundleSeed.Build();
        var exporter = withMentions ? new GcJsonlExporter(log, versions, mentions: mentions) : new GcJsonlExporter(log, versions);
        var result = await ExportBundleAssembler.AssembleAsync(
            [new GcBundleSource(exporter, GcFixtures.Owner, [GcFixtures.GcId])],
            new BundleAssemblyOptions
            {
                InstanceId = "inst-1",
                CreatedAt = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero),
                SigningKey = _key,
                OutputDirectory = Path.Combine(_root, withMentions ? "full" : "bare"),
            },
            TestContext.Current.CancellationToken);
        return result.Directory;
    }

    [Fact]
    public async Task A_full_exported_gc_bundle_passes_the_verifier()
    {
        var directory = await AssembleAsync();

        var report = BundleVerifier.Verify(directory, ct: TestContext.Current.CancellationToken);

        Assert.True(report.Verified, string.Join("; ", report.Problems));
        Assert.Equal(1, report.Gc!.Gcs);
        Assert.Equal(5, report.Gc.Events);
    }

    [Fact]
    public async Task A_bundle_without_mentions_still_passes()
    {
        var directory = await AssembleAsync(withMentions: false);

        var report = BundleVerifier.Verify(directory, ct: TestContext.Current.CancellationToken);

        Assert.True(report.Verified, string.Join("; ", report.Problems));
    }

    [Fact]
    public async Task A_changed_event_in_an_exported_bundle_fails_the_verifier()
    {
        var directory = await AssembleAsync();
        var path = Path.Combine(directory, "gc", GcFixtures.GcId.ToString("D"), "events.jsonl");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, text.Replace("\"kind\":\"system\"", "\"kind\":\"sysTEM\"", StringComparison.Ordinal), new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var report = BundleVerifier.Verify(directory, ct: TestContext.Current.CancellationToken);

        Assert.False(report.Verified);
    }

    [Fact]
    public async Task A_wrong_mention_count_in_head_fails_with_a_head_mismatch()
    {
        var directory = await AssembleAsync();
        var headPath = Path.Combine(directory, "gc", GcFixtures.GcId.ToString("D"), "head.json");
        var head = await File.ReadAllTextAsync(headPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"mentionCount\":3", head, StringComparison.Ordinal);
        await File.WriteAllTextAsync(headPath, head.Replace("\"mentionCount\":3", "\"mentionCount\":2", StringComparison.Ordinal), new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var report = BundleVerifier.Verify(directory, ct: TestContext.Current.CancellationToken);

        Assert.Contains(report.Problems, p => p.Code == "gc_head_mismatch");
    }
}
