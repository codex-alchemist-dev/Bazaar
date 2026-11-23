using System.Security.Cryptography;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Testing;

/// <summary>
/// Read-only access to the files a checksum scan covers. The server supplies the real one over <c>kodo-data</c> and
/// <c>kodo-evidence</c>; the core never touches a disk. There is deliberately no write method: a scan cannot repair anything.
/// </summary>
public interface ISelfCheckFileSource
{
    /// <summary>Every file path under <paramref name="root"/>, recursively, in any order. Throws when the root cannot be listed.</summary>
    IAsyncEnumerable<string> EnumerateFilesAsync(string root, CancellationToken cancellationToken);

    /// <summary>Opens a file for reading, or returns null when it no longer exists. Throws when it exists but cannot be read.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Where the recorded hashes come from (the last known-good state of the data directories).</summary>
public interface IChecksumManifestStore
{
    /// <summary>The recorded hashes, or null when none have been recorded yet.</summary>
    Task<ChecksumManifest?> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Recorded SHA-256 hashes by path. Paths compare ordinally; hashes are compared as lowercase hex.</summary>
public sealed class ChecksumManifest
{
    private readonly Dictionary<string, string> _hashes;

    public ChecksumManifest(IEnumerable<KeyValuePair<string, string>> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        _hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, string hash) in hashes)
        {
            _hashes[path] = hash.ToLowerInvariant();
        }
    }

    public IReadOnlyDictionary<string, string> Hashes => _hashes;

    /// <summary>The lowercase hex SHA-256 of <paramref name="content"/>, in the format the manifest records.</summary>
    public static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>The result of one scan. Paths are sorted so two scans of the same state give the same report.</summary>
public sealed record ChecksumScanReport(
    int Scanned,
    IReadOnlyList<string> Mismatched,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unrecorded,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>True when no file was changed, removed, unrecorded or unreadable.</summary>
    public bool IsClean => Mismatched.Count == 0 && Missing.Count == 0 && Unrecorded.Count == 0 && Unreadable.Count == 0;
}

/// <summary>
/// The scan behind <c>selfcheck.checksums</c> (doc 46 section 17.2): hashes every file under the roots and compares against the
/// recorded hashes. A mismatch is reported, not repaired.
/// </summary>
public static class ChecksumScanner
{
    public static async Task<ChecksumScanReport> ScanAsync(
        IReadOnlyList<string> roots,
        ChecksumManifest manifest,
        ISelfCheckFileSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> mismatched = [];
        List<string> unrecorded = [];
        List<string> unreadable = [];
        int scanned = 0;

        foreach (string root in roots)
        {
            List<string> files = [];
            try
            {
                await foreach (string path in source.EnumerateFilesAsync(root, cancellationToken).ConfigureAwait(false))
                {
                    files.Add(path);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                unreadable.Add(root);
                continue;
            }

            foreach (string path in files)
            {
                if (!seen.Add(path))
                {
                    continue;
                }

                try
                {
                    Stream? stream = await source.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
                    if (stream is null)
                    {
                        // Gone between listing and opening: it is reported as missing below if it was recorded.
                        seen.Remove(path);
                        continue;
                    }

                    string actual;
                    await using (stream.ConfigureAwait(false))
                    {
                        actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    }

                    scanned++;
                    if (!manifest.Hashes.TryGetValue(path, out string? recorded))
                    {
                        unrecorded.Add(path);
                    }
                    else if (!string.Equals(recorded, actual, StringComparison.Ordinal))
                    {
                        mismatched.Add(path);
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    unreadable.Add(path);
                }
            }
        }

        // A recorded path under a scanned root that was neither found nor unreadable (itself or through its root) is missing.
        // Paths outside every root are not this scan's business.
        List<string> missing = manifest.Hashes.Keys
            .Where(path => !seen.Contains(path)
                && !unreadable.Contains(path)
                && !unreadable.Any(root => IsUnder(path, root))
                && roots.Any(root => IsUnder(path, root)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return new ChecksumScanReport(
            scanned,
            Sorted(mismatched),
            missing,
            Sorted(unrecorded),
            Sorted(unreadable));
    }

    private static List<string> Sorted(List<string> paths) => paths.OrderBy(path => path, StringComparer.Ordinal).ToList();

    private static bool IsUnder(string path, string root)
    {
        string prefix = root.TrimEnd('/', '\\');
        return path.StartsWith(prefix, StringComparison.Ordinal)
            && (path.Length == prefix.Length || path[prefix.Length] is '/' or '\\');
    }
}

/// <summary>
/// <c>selfcheck.checksums</c> (doc 46 sections 9.1 and 17.2), non-critical, daily. Red for any mismatch or missing file, unknown
/// when it cannot compare, amber for unrecorded files, otherwise green.
/// </summary>
public sealed class ChecksumSelfCheck : ISelfCheck
{
    public const string Id = "selfcheck.checksums";

    private readonly IReadOnlyList<string> _roots;
    private readonly IChecksumManifestStore _manifests;
    private readonly ISelfCheckFileSource _source;

    public ChecksumSelfCheck(IReadOnlyList<string> roots, IChecksumManifestStore manifests, ISelfCheckFileSource source)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(source);
        _roots = roots;
        _manifests = manifests;
        _source = source;
    }

    string ISelfCheck.Id => Id;

    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        ChecksumManifest? manifest;
        try
        {
            manifest = await _manifests.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unknown(SelfCheckCauses.ManifestMissing);
        }

        if (manifest is null)
        {
            return ProbeResult.Unknown(SelfCheckCauses.ManifestMissing);
        }

        ChecksumScanReport report = await ChecksumScanner.ScanAsync(_roots, manifest, _source, cancellationToken).ConfigureAwait(false);
        return ToResult(report);
    }

    /// <summary>The pure mapping from a report to a probe result. Integrity findings outrank unreadable files.</summary>
    public static ProbeResult ToResult(ChecksumScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Mismatched.Count > 0 || report.Missing.Count > 0)
        {
            return ProbeResult.Red(SelfCheckCauses.IntegrityMismatch);
        }

        if (report.Unreadable.Count > 0)
        {
            return ProbeResult.Unknown(SelfCheckCauses.ScanUnreadable);
        }

        return report.Unrecorded.Count > 0 ? ProbeResult.Amber(SelfCheckCauses.UnrecordedFiles) : ProbeResult.Green();
    }
}
