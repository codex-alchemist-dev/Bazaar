using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Canaries;

/// <summary>One planted canary. Only the fingerprint of its value is kept, never the value.</summary>
internal sealed record PlantedCanary(Guid MountId, Guid AgentId, Guid VmId, string RelativePath, string Fingerprint);

/// <summary>
/// Plants canary credentials in a Soul mount and remembers which fingerprint belongs to which mount. Values are generated at run
/// time from the system random source. They live in the files on the share; this class keeps only SHA-256 fingerprints.
/// </summary>
internal sealed class CanaryRegistry
{
    public const string FolderName = ".canary";

    private static readonly string[] _fileNames = ["credentials.json", "session-backup.txt", "api-tokens.env", "recovery-notes.txt"];

    private readonly object _gate = new();
    private readonly Dictionary<string, PlantedCanary> _byFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<PlantedCanary>> _byMount = [];
    private readonly Dictionary<Guid, IAsyncDisposable> _watches = [];

    public static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Removes any earlier canary folder, then writes fresh canaries under <c>mountRoot/.canary</c>.</summary>
    public IReadOnlyList<PlantedCanary> Plant(MountSpec spec, Guid mountId, string mountRoot, int count, IMountContentPolicy policy)
    {
        var folder = Path.Combine(mountRoot, FolderName);
        RemoveFolder(mountRoot);
        Directory.CreateDirectory(folder);

        var planted = new List<PlantedCanary>();
        foreach (var name in _fileNames.Take(Math.Max(0, count)))
        {
            var relative = $"{FolderName}/{name}";
            if (!policy.Evaluate(new MountContentCandidate(MountKind.Soul, MountContentOrigin.Canary, relative, 0)).Allowed)
                continue;

            var value = "bzc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            File.WriteAllText(Path.Combine(folder, name), $"{{\"token\":\"{value}\"}}\n", new UTF8Encoding(false));
            planted.Add(new PlantedCanary(mountId, spec.AgentId, spec.VmId, relative, Fingerprint(value)));
        }

        lock (_gate)
        {
            _byMount[mountId] = planted;
            foreach (var canary in planted)
                _byFingerprint[canary.Fingerprint] = canary;
        }

        return planted;
    }

    public void SetWatch(Guid mountId, IAsyncDisposable watch)
    {
        lock (_gate)
            _watches[mountId] = watch;
    }

    public PlantedCanary? FindByFingerprint(string fingerprint)
    {
        lock (_gate)
            return _byFingerprint.GetValueOrDefault(fingerprint);
    }

    public PlantedCanary? FindByPath(Guid mountId, string relativePath)
    {
        lock (_gate)
            return _byMount.TryGetValue(mountId, out var list) ? list.FirstOrDefault(c => c.RelativePath == relativePath) : null;
    }

    /// <summary>Forgets a mount's canaries and stops its watch. Does not touch files; see <see cref="RemoveFolder"/>.</summary>
    public async ValueTask ForgetAsync(Guid mountId)
    {
        IAsyncDisposable? watch;
        lock (_gate)
        {
            if (_byMount.Remove(mountId, out var list))
            {
                foreach (var canary in list)
                    _byFingerprint.Remove(canary.Fingerprint);
            }
            _watches.Remove(mountId, out watch);
        }

        if (watch is not null)
            await watch.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Deletes the canary folder of a mount root, best effort. Canaries must not travel with a Soul export or backup.</summary>
    public static void RemoveFolder(string mountRoot)
    {
        try
        {
            var folder = Path.Combine(mountRoot, FolderName);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
