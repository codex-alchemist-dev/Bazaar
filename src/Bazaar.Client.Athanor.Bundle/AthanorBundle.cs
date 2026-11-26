using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Athanor.Bundle;

/// <summary>The outcome of checking a bundle folder against the pinned lock.</summary>
/// <param name="Complete">True when every required file exists and the Monaco files hash to the pinned value.</param>
/// <param name="Problem">Why the bundle cannot be used, or null when it can.</param>
public sealed record BundleVerification(bool Complete, string? Problem);

/// <summary>
/// Locates and checks the Athanor editor bundle (doc 57 §2): the host page, the bridge script and the trimmed, pinned
/// Monaco release. The lock (version, tarball hash, trimmed-bundle hash) is embedded in this assembly, so a changed file
/// on disk cannot also change the value it is checked against.
/// </summary>
public static class AthanorBundle
{
    /// <summary>The page a host loads first.</summary>
    public const string HostPage = "host.html";

    /// <summary>The script that implements the editor side of the bridge.</summary>
    public const string BridgeScript = "bridge.js";

    private const string _lockResource = "monaco.lock.json";
    private const string _stampFile = ".stamp";

    private static readonly Lazy<MonacoLock> _lock = new(ReadLock);

    /// <summary>The Monaco release the bundle is pinned to.</summary>
    public static string MonacoVersion => _lock.Value.Version;

    /// <summary>The licence of the vendored Monaco files.</summary>
    public static string MonacoLicense => _lock.Value.License;

    /// <summary>The sha256 (lower-case hex) over the trimmed Monaco files, as pinned in the lock.</summary>
    public static string PinnedBundleSha256 => _lock.Value.BundleSha256;

    /// <summary>The folder the build copies the bundle to, next to this assembly.</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "athanor-bundle");

    /// <summary>The files a bundle needs before a host can load it, relative to the bundle root.</summary>
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        HostPage,
        BridgeScript,
        "monaco/vs/loader.js",
        "monaco/vs/editor.js",
        "monaco/vs/editor/editor.main.css",
        "monaco/vs/editor/editor.worker.js",
        "monaco/vs/basic-languages/monaco.contribution.js",
        "monaco/LICENSE",
    ];

    /// <summary>
    /// Checks that <paramref name="root"/> holds every required file and that the Monaco files hash to the pinned value.
    /// Hashes the files that make up the trimmed bundle (about 5 MB), so call it once per client start, not per window.
    /// </summary>
    public static BundleVerification Verify(string? root = null)
    {
        root ??= DefaultRoot;
        foreach (string required in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(root, required)))
            {
                return new BundleVerification(false, $"The bundle is missing {required}. Run scripts/fetch-monaco.ps1 in Bazaar.Client.Athanor.Bundle.");
            }
        }

        string vsRoot = Path.Combine(root, "monaco", "vs");
        string actual = ComputeBundleSha256(vsRoot, _lock.Value.Include);
        return actual.Equals(_lock.Value.BundleSha256, StringComparison.Ordinal)
            ? new BundleVerification(true, null)
            : new BundleVerification(false, "The Monaco files do not match the pinned hash in monaco.lock.json.");
    }

    /// <summary>
    /// The hash the fetch script pins: sha256 over the sorted lines <c>relative/path sha256-of-file</c> (forward slashes,
    /// each line ending in a line feed), for the files whose path matches an include pattern.
    /// </summary>
    public static string ComputeBundleSha256(string vsRoot, IEnumerable<string> includePatterns)
    {
        ArgumentNullException.ThrowIfNull(includePatterns);
        string[] patterns = [.. includePatterns];
        var lines = new List<string>();
        foreach (string file in Directory.EnumerateFiles(vsRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(vsRoot, file).Replace('\\', '/');
            if (patterns.Any(p => Matches(p, relative)))
            {
                lines.Add($"{relative} {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        byte[] manifest = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        return Convert.ToHexStringLower(SHA256.HashData(manifest));
    }

    /// <summary>The stamp the fetch script leaves next to the Monaco files (its bundle hash), or null when it is absent.</summary>
    public static string? ReadStamp(string? root = null)
    {
        string path = Path.Combine(root ?? DefaultRoot, "monaco", _stampFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>PowerShell <c>-like</c> semantics for the lock's patterns: <c>*</c> matches any run of characters, including a slash.</summary>
    private static bool Matches(string pattern, string path)
    {
        int p = 0;
        int s = 0;
        int star = -1;
        int mark = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = s;
            }
            else if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(path[s]))
            {
                p++;
                s++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                s = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static MonacoLock ReadLock()
    {
        using Stream stream = typeof(AthanorBundle).Assembly.GetManifestResourceStream(_lockResource)
            ?? throw new InvalidOperationException("The embedded monaco.lock.json is missing.");
        using JsonDocument json = JsonDocument.Parse(stream);
        JsonElement root = json.RootElement;
        return new MonacoLock(
            root.GetProperty("version").GetString()!,
            root.GetProperty("license").GetString()!,
            root.GetProperty("bundleSha256").GetString()!,
            [.. root.GetProperty("include").EnumerateArray().Select(e => e.GetString()!)]);
    }

    private sealed record MonacoLock(string Version, string License, string BundleSha256, string[] Include);
}
