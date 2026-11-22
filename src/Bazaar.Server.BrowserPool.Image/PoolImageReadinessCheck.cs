using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Image;

/// <summary>The file system the readiness check looks at, as a port so tests need no pool VM. Methods may throw; the check fails closed on that.</summary>
public interface IPoolImageFileSystem
{
    /// <summary>True when a file exists at the path.</summary>
    bool FileExists(string path);

    /// <summary>True when a directory exists at the path.</summary>
    bool DirectoryExists(string path);

    /// <summary>The Unix permission bits of the file or directory (symbolic links followed), or null when they cannot be known.</summary>
    UnixFileMode? GetUnixMode(string path);

    /// <summary>Reads a small text file.</summary>
    string ReadAllText(string path);
}

/// <summary>The real file system. Permission bits are available on Unix only.</summary>
internal sealed class PhysicalPoolImageFileSystem : IPoolImageFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public UnixFileMode? GetUnixMode(string path) => OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

    public string ReadAllText(string path) => File.ReadAllText(path);
}

/// <summary>
/// Runs on the pool VM and checks every <see cref="PoolImageRequirements"/> entry (docs/design/59, section 11). It fails closed: any
/// error while looking counts as a failure. Failures carry an id and a plain reason, never file contents. A passing result is reused
/// for <see cref="PoolImageOptions.RecheckInterval"/>; a failure is never reused, so a fixed image is accepted at once.
/// </summary>
public sealed class PoolImageReadinessCheck : IPoolReadinessCheck
{
    /// <summary>The kernel setting that restricts unprivileged user namespaces on Ubuntu 24.04.</summary>
    internal const string RestrictionPath = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";

    /// <summary>The list of loaded AppArmor profiles.</summary>
    internal const string LoadedProfilesPath = "/sys/kernel/security/apparmor/profiles";

    private const string _searchPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private const UnixFileMode _anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode _groupOtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
    private const UnixFileMode _groupOtherExecute = UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly PoolImageRequirements _requirements;
    private readonly IPoolImageFileSystem _fs;
    private readonly PoolImageOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private DateTimeOffset? _passedUntil;

    /// <summary>Creates the check.</summary>
    public PoolImageReadinessCheck(PoolImageRequirements requirements, IPoolImageFileSystem fileSystem, PoolImageOptions options, TimeProvider time)
    {
        _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PoolReadinessFailure>> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_passedUntil is { } until && now < until)
            {
                return Task.FromResult<IReadOnlyList<PoolReadinessFailure>>([]);
            }
        }

        var failures = new List<PoolReadinessFailure>();
        foreach (var requirement in _requirements.Items)
        {
            string? reason;
            try
            {
                reason = Check(requirement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reason = "It could not be checked (" + ex.GetType().Name + ").";
            }

            if (reason is not null)
            {
                failures.Add(new PoolReadinessFailure(requirement.Id, reason));
            }
        }

        if (failures.Count == 0)
        {
            lock (_lock)
            {
                _passedUntil = now + _options.RecheckInterval;
            }
        }
        else
        {
            lock (_lock)
            {
                _passedUntil = null;
            }
        }

        return Task.FromResult<IReadOnlyList<PoolReadinessFailure>>(failures);
    }

    private string? Check(PoolImageRequirement r) => r.Kind switch
    {
        PoolImageRequirementKind.Program => CheckProgram(r),
        PoolImageRequirementKind.Directory => CheckDirectory(r),
        PoolImageRequirementKind.AppArmorProfile => CheckAppArmor(),
        _ => "Unknown requirement kind.",
    };

    private string? CheckProgram(PoolImageRequirement r)
    {
        string? found = Resolve(r.Path);
        if (found is null)
        {
            return $"Program not found at {r.Path}.";
        }

        UnixFileMode? mode = _fs.GetUnixMode(found);
        if (mode is null)
        {
            return $"The permissions of {found} cannot be read.";
        }

        if ((mode.Value & _anyExecute) == 0)
        {
            return $"{found} is not executable.";
        }

        if (r.SlotsMustExecute && (mode.Value & UnixFileMode.OtherExecute) == 0)
        {
            return $"{found} must be executable by the slot users.";
        }

        return null;
    }

    private string? Resolve(string path)
    {
        if (path.StartsWith('/'))
        {
            return _fs.FileExists(path) ? path : null;
        }

        foreach (var dir in _searchPath.Split(':'))
        {
            string candidate = dir + "/" + path;
            if (_fs.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? CheckDirectory(PoolImageRequirement r)
    {
        if (!_fs.DirectoryExists(r.Path))
        {
            return $"Directory {r.Path} does not exist.";
        }

        UnixFileMode? mode = _fs.GetUnixMode(r.Path);
        if (mode is null)
        {
            return $"The permissions of {r.Path} cannot be read.";
        }

        // Slots may traverse the slot root but never list or write it; the pipe parent is closed to everyone but root.
        UnixFileMode forbidden = r.SlotsMustExecute ? _groupOtherAccess : _groupOtherAccess | _groupOtherExecute;
        if ((mode.Value & forbidden) != 0)
        {
            return $"{r.Path} is open to more than the slot users may have.";
        }

        if (r.SlotsMustExecute && (mode.Value & UnixFileMode.OtherExecute) == 0)
        {
            return $"{r.Path} must be traversable by the slot users.";
        }

        return null;
    }

    private string? CheckAppArmor()
    {
        // The profile is needed only where the system restricts unprivileged user namespaces. An unreadable or unclear
        // setting counts as restricted, and a loaded-profile list that cannot be read counts as missing.
        bool restricted = _fs.FileExists(RestrictionPath) && _fs.ReadAllText(RestrictionPath).Trim() != "0";

        if (!restricted)
        {
            return null;
        }

        if (!_fs.FileExists(LoadedProfilesPath))
        {
            return "User namespaces are restricted and the list of loaded AppArmor profiles is not available.";
        }

        foreach (var line in _fs.ReadAllText(LoadedProfilesPath).Split('\n'))
        {
            if (line.StartsWith(BwrapAppArmorProfile.ProfileName + " ", StringComparison.Ordinal))
            {
                return null;
            }
        }

        return $"User namespaces are restricted and the AppArmor profile {BwrapAppArmorProfile.ProfileName} is not loaded.";
    }
}
