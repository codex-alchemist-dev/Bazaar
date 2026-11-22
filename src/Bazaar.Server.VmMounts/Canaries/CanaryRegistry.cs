using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Canaries;

/// <summary>
/// Plants canary credentials in a Soul mount and holds the live provider watches. The fingerprints themselves are persisted through
/// <see cref="ICanaryStore"/>, so a restart loses nothing but the in-memory watches, which <c>RearmWatchesAsync</c> restores. Values
/// are generated at run time from the system random source and live only in the files on the share.
/// </summary>
internal sealed class CanaryRegistry
{
    public const string FolderName = ".canary";

    private static readonly string[] _fileNames = ["credentials.json", "session-backup.txt", "api-tokens.env", "recovery-notes.txt"];

    private readonly object _gate = new();
    private readonly Dictionary<Guid, IAsyncDisposable> _watches = [];

    public static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Removes any earlier canary folder, then writes fresh canaries under <c>mountRoot/.canary</c>.</summary>
    public static IReadOnlyList<CanaryRecord> Plant(
        MountSpec spec, Guid mountId, string mountRoot, int count, IMountContentPolicy policy, DateTimeOffset now)
    {
        var folder = Path.Combine(mountRoot, FolderName);
        RemoveFolder(mountRoot);
        Directory.CreateDirectory(folder);

        var planted = new List<CanaryRecord>();
        foreach (var name in _fileNames.Take(Math.Max(0, count)))
        {
            var relative = $"{FolderName}/{name}";
            if (!policy.Evaluate(new MountContentCandidate(MountKind.Soul, MountContentOrigin.Canary, relative, 0)).Allowed)
                continue;

            var value = "bzc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            File.WriteAllText(Path.Combine(folder, name), $"{{\"token\":\"{value}\"}}\n", new UTF8Encoding(false));
            planted.Add(new CanaryRecord(Guid.CreateVersion7(), mountId, spec.AgentId, spec.VmId, relative, Fingerprint(value), now, null));
        }

        return planted;
    }

    public async ValueTask SetWatchAsync(Guid mountId, IAsyncDisposable watch)
    {
        IAsyncDisposable? previous;
        lock (_gate)
        {
            _watches.Remove(mountId, out previous);
            _watches[mountId] = watch;
        }

        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Stops a mount's watch. The persisted canary records are retired separately, never deleted.</summary>
    public async ValueTask StopWatchAsync(Guid mountId)
    {
        IAsyncDisposable? watch;
        lock (_gate)
            _watches.Remove(mountId, out watch);
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
