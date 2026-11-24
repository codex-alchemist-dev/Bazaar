using System.Text;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Testing;

namespace Bazaar.Kodo.Tests.Testing;

internal sealed class FakeFileSource : ISelfCheckFileSource
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    public HashSet<string> BrokenRoots { get; } = new(StringComparer.Ordinal);

    public HashSet<string> VanishOnOpen { get; } = new(StringComparer.Ordinal);

    public int Opens { get; private set; }

    public async IAsyncEnumerable<string> EnumerateFilesAsync(
        string root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (BrokenRoots.Contains(root))
        {
            throw new IOException("root unreadable");
        }

        foreach (string path in Files.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (path.StartsWith(root, StringComparison.Ordinal))
            {
                yield return path;
            }
        }

        foreach (string path in VanishOnOpen)
        {
            yield return path;
        }
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Opens++;
        if (Unreadable.Contains(path))
        {
            throw new IOException("denied");
        }

        return Task.FromResult<Stream?>(Files.TryGetValue(path, out byte[]? content) ? new MemoryStream(content) : null);
    }
}

internal sealed class FakeManifestStore(ChecksumManifest? manifest, bool throws = false) : IChecksumManifestStore
{
    public Task<ChecksumManifest?> LoadAsync(CancellationToken cancellationToken) =>
        throws ? throw new IOException("store down") : Task.FromResult(manifest);
}

/// <summary><c>selfcheck.checksums</c> and the scan behind it (doc 46 section 17.2).</summary>
public sealed class ChecksumSelfCheckTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static (FakeFileSource Source, ChecksumManifest Manifest) Baseline()
    {
        FakeFileSource source = new();
        source.Files["/data/a.bin"] = Bytes("alpha");
        source.Files["/data/sub/b.bin"] = Bytes("beta");
        source.Files["/evidence/e1.json"] = Bytes("evidence");
        ChecksumManifest manifest = new(source.Files.Select(f => KeyValuePair.Create(f.Key, ChecksumManifest.Hash(f.Value))));
        return (source, manifest);
    }

    private static Task<ProbeResult> RunAsync(FakeFileSource source, ChecksumManifest? manifest, bool storeThrows = false) =>
        new ChecksumSelfCheck(["/data", "/evidence"], new FakeManifestStore(manifest, storeThrows), source).RunAsync(_ct);

    [Fact]
    public async Task UnchangedFilesAreGreen()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();

        ProbeResult result = await RunAsync(source, manifest);

        Assert.Equal(ProbeStatus.Green, result.Status);
    }

    [Fact]
    public async Task OneChangedByteIsAnIntegrityFindingAndNothingIsRepaired()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.Files["/data/a.bin"] = Bytes("alphA");

        ProbeResult result = await RunAsync(source, manifest);

        Assert.Equal(ProbeStatus.Red, result.Status);
        Assert.Equal(SelfCheckCauses.IntegrityMismatch, result.Cause);
        Assert.Equal(Bytes("alphA"), source.Files["/data/a.bin"]);
    }

    [Fact]
    public async Task ARecordedFileThatIsGoneIsRed()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.Files.Remove("/evidence/e1.json");

        ProbeResult result = await RunAsync(source, manifest);

        Assert.Equal(ProbeStatus.Red, result.Status);
        Assert.Equal(SelfCheckCauses.IntegrityMismatch, result.Cause);
    }

    [Fact]
    public async Task AFileThatVanishesBetweenListingAndOpeningCountsAsMissing()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.VanishOnOpen.Add("/data/ghost.bin");
        ChecksumManifest withGhost = new(manifest.Hashes.Append(KeyValuePair.Create("/data/ghost.bin", ChecksumManifest.Hash(Bytes("x")))));

        ChecksumScanReport report = await ChecksumScanner.ScanAsync(["/data", "/evidence"], withGhost, source, _ct);

        Assert.Equal(["/data/ghost.bin"], report.Missing);
        Assert.Equal(3, report.Scanned);
    }

    [Fact]
    public async Task NewUnrecordedFilesAreAmber()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.Files["/data/new.bin"] = Bytes("new");

        ProbeResult result = await RunAsync(source, manifest);

        Assert.Equal(ProbeStatus.Amber, result.Status);
        Assert.Equal(SelfCheckCauses.UnrecordedFiles, result.Cause);
    }

    [Fact]
    public async Task AnUnreadableFileIsUnknownNeverGreen()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.Unreadable.Add("/data/a.bin");

        ChecksumScanReport report = await ChecksumScanner.ScanAsync(["/data", "/evidence"], manifest, source, _ct);
        ProbeResult result = await RunAsync(source, manifest);

        Assert.Equal(["/data/a.bin"], report.Unreadable);
        Assert.Empty(report.Missing);
        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(SelfCheckCauses.ScanUnreadable, result.Cause);
    }

    [Fact]
    public async Task AnUnlistableRootIsUnknown()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.BrokenRoots.Add("/evidence");

        ChecksumScanReport report = await ChecksumScanner.ScanAsync(["/data", "/evidence"], manifest, source, _ct);

        Assert.Equal(["/evidence"], report.Unreadable);
        Assert.Empty(report.Missing);
        Assert.Equal(ProbeStatus.Unknown, ChecksumSelfCheck.ToResult(report).Status);
    }

    [Fact]
    public void AMismatchOutranksUnreadableFiles()
    {
        ChecksumScanReport report = new(1, ["/data/a.bin"], [], [], ["/data/b.bin"]);

        Assert.Equal(ProbeStatus.Red, ChecksumSelfCheck.ToResult(report).Status);
    }

    [Fact]
    public async Task NoRecordedHashesIsUnknownNeverGreen()
    {
        (FakeFileSource source, _) = Baseline();

        ProbeResult missing = await RunAsync(source, null);
        ProbeResult throwing = await RunAsync(source, null, storeThrows: true);

        Assert.Equal((ProbeStatus.Unknown, SelfCheckCauses.ManifestMissing), (missing.Status, missing.Cause));
        Assert.Equal((ProbeStatus.Unknown, SelfCheckCauses.ManifestMissing), (throwing.Status, throwing.Cause));
        Assert.Equal(0, source.Opens);
    }

    [Fact]
    public async Task RecordedPathsOutsideTheScannedRootsAreNotMissing()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        ChecksumManifest wider = new(manifest.Hashes.Append(KeyValuePair.Create("/data2/x.bin", ChecksumManifest.Hash(Bytes("x")))));

        ChecksumScanReport report = await ChecksumScanner.ScanAsync(["/data", "/evidence"], wider, source, _ct);

        Assert.Empty(report.Missing);
    }

    [Fact]
    public async Task ManifestHashesCompareCaseInsensitively()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        ChecksumManifest upper = new(manifest.Hashes.Select(h => KeyValuePair.Create(h.Key, h.Value.ToUpperInvariant())));

        ProbeResult result = await RunAsync(source, upper);

        Assert.Equal(ProbeStatus.Green, result.Status);
    }

    [Fact]
    public async Task OverlappingRootsScanEachFileOnce()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();

        ChecksumScanReport report = await ChecksumScanner.ScanAsync(["/data", "/data/sub", "/evidence"], manifest, source, _ct);

        Assert.Equal(3, report.Scanned);
        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task ReportListsAreSortedSoTwoScansAgree()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        source.Files["/data/z.bin"] = Bytes("z");
        source.Files["/data/m.bin"] = Bytes("m");
        source.Files["/data/a.bin"] = Bytes("changed");
        source.Files["/data/sub/b.bin"] = Bytes("changed");

        ChecksumScanReport first = await ChecksumScanner.ScanAsync(["/data"], manifest, source, _ct);
        ChecksumScanReport second = await ChecksumScanner.ScanAsync(["/data"], manifest, source, _ct);

        Assert.Equal(["/data/a.bin", "/data/sub/b.bin"], first.Mismatched);
        Assert.Equal(["/data/m.bin", "/data/z.bin"], first.Unrecorded);
        Assert.Equal(first.Mismatched, second.Mismatched);
        Assert.False(first.IsClean);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        (FakeFileSource source, ChecksumManifest manifest) = Baseline();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ChecksumSelfCheck(["/data"], new FakeManifestStore(manifest), source).RunAsync(cts.Token));
    }

    [Fact]
    public void TheIdIsTheCatalogueId()
    {
        ISelfCheck check = new ChecksumSelfCheck([], new FakeManifestStore(null), new FakeFileSource());

        Assert.Equal("selfcheck.checksums", check.Id);
    }
}
