using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>An in-memory <see cref="IScopedMountStore"/> with the one-attached-mount-per-VM-and-guest-path rule and the VM checks of the database.</summary>
public sealed class InMemoryScopedMountStore : IScopedMountStore
{
    private readonly object _gate = new();
    private readonly List<ScopedMountRow> _rows = [];
    private readonly Dictionary<Guid, (Guid Agent, GuestOs Os, bool Retired)> _vms = [];

    public IReadOnlyList<ScopedMountRow> All
    {
        get
        {
            lock (_gate)
                return [.. _rows];
        }
    }

    public void RegisterVm(Guid vmId, Guid agentId, GuestOs os = GuestOs.Linux, bool retired = false)
    {
        lock (_gate)
            _vms[vmId] = (agentId, os, retired);
    }

    public Task<MountInsertResult> TryInsertAsync(ScopedMountRow row, GuestOs guestOs, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_vms.TryGetValue(row.VmId, out var vm) || vm.Agent != row.AgentId || vm.Os != guestOs || vm.Retired)
                return Task.FromResult(MountInsertResult.VmNotEligible);
            if (_rows.Any(r => r.DetachedAt is null && r.VmId == row.VmId && r.GuestPath == row.GuestPath))
                return Task.FromResult(MountInsertResult.GuestPathTaken);
            _rows.Add(row);
            return Task.FromResult(MountInsertResult.Inserted);
        }
    }

    public Task<GuestOs?> GetVmGuestOsAsync(Guid vmId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<GuestOs?>(_vms.TryGetValue(vmId, out var vm) ? vm.Os : null);
    }

    public Task<ScopedMountRow?> GetAsync(Guid mountId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_rows.FirstOrDefault(r => r.Id == mountId));
    }

    public Task<IReadOnlyList<ScopedMountRow>> ListAttachedAsync(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ScopedMountRow>>([.. _rows.Where(r => r.DetachedAt is null).OrderBy(r => r.Id)]);
    }

    public Task<IReadOnlyList<ScopedMountRow>> ListAttachedForAgentAsync(Guid agentId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ScopedMountRow>>([.. _rows.Where(r => r.AgentId == agentId && r.DetachedAt is null).OrderBy(r => r.Id)]);
    }

    public Task<bool> MarkDetachedAsync(Guid mountId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _rows.FindIndex(r => r.Id == mountId && r.DetachedAt is null);
            if (i < 0)
                return Task.FromResult(false);
            _rows[i] = _rows[i] with { DetachedAt = at };
            return Task.FromResult(true);
        }
    }
}

public sealed class InMemoryCanaryStore : ICanaryStore
{
    private readonly object _gate = new();
    private readonly List<CanaryRecord> _rows = [];

    public IReadOnlyList<CanaryRecord> All
    {
        get
        {
            lock (_gate)
                return [.. _rows];
        }
    }

    public Task InsertAsync(IReadOnlyList<CanaryRecord> canaries, CancellationToken ct)
    {
        lock (_gate)
            _rows.AddRange(canaries);
        return Task.CompletedTask;
    }

    public Task<CanaryRecord?> FindByFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_rows.FirstOrDefault(r => r.Fingerprint == fingerprint));
    }

    public Task<IReadOnlyList<CanaryRecord>> ListActiveForMountAsync(Guid mountId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CanaryRecord>>([.. _rows.Where(r => r.MountId == mountId && r.RetiredAt is null)]);
    }

    public Task RetireForMountAsync(Guid mountId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].MountId == mountId && _rows[i].RetiredAt is null)
                    _rows[i] = _rows[i] with { RetiredAt = at };
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>An attach guard that records its calls and the provider calls seen so far, and can refuse.</summary>
public sealed class FakeAttachGuard(Func<IReadOnlyList<string>>? providerCalls = null) : IMountAttachGuard
{
    private int _calls;

    public int Calls => _calls;

    /// <summary>How many provider calls had happened when the guard was last asked. Must be 0: the guard runs before any provider work.</summary>
    public int ProviderCallsSeen { get; private set; }

    public bool Refuse { get; set; }

    public Task CheckSoulAttachAsync(Guid vmId, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ProviderCallsSeen = providerCalls?.Invoke().Count ?? 0;
        return Refuse
            ? throw new MountRefusedException(MountRefusalReason.AttachGuardRefused, "An anonymous browser runs on this VM.")
            : Task.CompletedTask;
    }
}

public sealed class FakeGuestScrubber : IGuestScrubber
{
    private readonly ConcurrentQueue<GuestScrubRequest> _requests = new();

    public IReadOnlyList<GuestScrubRequest> Requests => [.. _requests];

    public Exception? Fail { get; set; }

    public Task ScrubAsync(GuestScrubRequest request, CancellationToken ct)
    {
        _requests.Enqueue(request);
        return Fail is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}

/// <summary>Reports a hard link count for files whose name is in <see cref="Linked"/>, and null (cannot tell) for the rest.</summary>
public sealed class FakeHardLinkProbe : IHardLinkProbe
{
    public HashSet<string> Linked { get; } = [];

    public int? GetLinkCount(string filePath) => Linked.Contains(Path.GetFileName(filePath)) ? 2 : null;
}

/// <summary>Throws from the audit log for the named kinds only.</summary>
public sealed class SelectiveThrowingAudit(params string[] kinds) : IAuditLog
{
    public Task AppendAsync(AuditEntry e, CancellationToken ct = default) =>
        kinds.Contains(e.Kind) ? throw new InvalidOperationException("audit store down") : Task.CompletedTask;
}

public sealed class RecordingAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public IReadOnlyList<AuditEntry> Entries => [.. _entries];

    public IReadOnlyList<AuditEntry> OfKind(string kind) => [.. _entries.Where(e => e.Kind == kind)];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        _entries.Enqueue(e);
        return Task.CompletedTask;
    }
}

public sealed class RecordingCanarySink : ICanarySink
{
    private readonly ConcurrentQueue<CanaryUseReport> _reports = new();

    public IReadOnlyList<CanaryUseReport> Reports => [.. _reports];

    public ValueTask ReportCanaryUseAsync(CanaryUseReport report, CancellationToken ct)
    {
        _reports.Enqueue(report);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A guest file system in memory, with hooks to play a hostile or broken guest.</summary>
public sealed class FakeGuestChannel : IMountGuestChannel
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private int _puts;
    private int _gets;
    private int _lists;

    public int PutCalls => _puts;

    public int GetCalls => _gets;

    public int ListCalls => _lists;

    /// <summary>Replaces the bytes <see cref="GetFileAsync"/> returns, after the listing hash was computed.</summary>
    public Func<string, byte[], byte[]>? GetTransform { get; set; }

    /// <summary>Replaces the listing the guest reports.</summary>
    public Func<IReadOnlyList<GuestFileInfo>, IReadOnlyList<GuestFileInfo>>? ListTransform { get; set; }

    public Exception? FailPut { get; set; }

    /// <summary>When set, <see cref="DeleteDirectoryAsync"/> throws it.</summary>
    public Exception? FailDelete { get; set; }

    public int DeleteCalls => _deletes;

    private int _deletes;

    /// <summary>When set, <see cref="ListFilesAsync"/> waits until its token is cancelled, like a guest that never answers.</summary>
    public bool ListBlocksUntilCancelled { get; set; }

    /// <summary>Called when a <see cref="GetFileAsync"/> starts.</summary>
    public Action? OnGetStarted { get; set; }

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <summary>Writes a file directly into the guest, as the agent's browser would.</summary>
    public void GuestWrite(string path, byte[] bytes) => _files[path] = bytes;

    public async Task PutFileAsync(Guid vmId, string guestPath, Stream content, string sha256, CancellationToken ct)
    {
        Interlocked.Increment(ref _puts);
        if (FailPut is { } failure)
            throw failure;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
            throw new InvalidDataException("Guest refused: hash mismatch.");
        _files[guestPath] = bytes;
    }

    public async Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct)
    {
        Interlocked.Increment(ref _gets);
        OnGetStarted?.Invoke();
        var bytes = _files[guestPath];
        if (GetTransform is { } transform)
            bytes = transform(guestPath, bytes);
        await sink.WriteAsync(bytes, ct);
    }

    public async Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(Guid vmId, string guestDirectory, CancellationToken ct)
    {
        Interlocked.Increment(ref _lists);
        if (ListBlocksUntilCancelled)
            await Task.Delay(Timeout.Infinite, ct);
        var prefix = guestDirectory.TrimEnd('/') + "/";
        IReadOnlyList<GuestFileInfo> listed =
        [
            .. _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new GuestFileInfo(f.Key[prefix.Length..], f.Value.Length, Convert.ToHexStringLower(SHA256.HashData(f.Value)))),
        ];
        return ListTransform is { } transform ? transform(listed) : listed;
    }

    public Task DeleteDirectoryAsync(Guid vmId, string guestDirectory, CancellationToken ct)
    {
        Interlocked.Increment(ref _deletes);
        if (FailDelete is { } failure)
            throw failure;
        var prefix = guestDirectory.TrimEnd('/') + "/";
        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
