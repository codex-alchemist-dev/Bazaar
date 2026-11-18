using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>An in-memory <see cref="IScopedMountStore"/> with the same one-attached-mount-per-VM-and-guest-path rule as the database.</summary>
public sealed class InMemoryScopedMountStore : IScopedMountStore
{
    private readonly object _gate = new();
    private readonly List<ScopedMountRow> _rows = [];

    public IReadOnlyList<ScopedMountRow> All
    {
        get
        {
            lock (_gate)
                return [.. _rows];
        }
    }

    public Task<bool> TryInsertAsync(ScopedMountRow row, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_rows.Any(r => r.DetachedAt is null && r.VmId == row.VmId && r.GuestPath == row.GuestPath))
                return Task.FromResult(false);
            _rows.Add(row);
            return Task.FromResult(true);
        }
    }

    public Task<ScopedMountRow?> GetAsync(Guid mountId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_rows.FirstOrDefault(r => r.Id == mountId));
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
        var bytes = _files[guestPath];
        if (GetTransform is { } transform)
            bytes = transform(guestPath, bytes);
        await sink.WriteAsync(bytes, ct);
    }

    public Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(Guid vmId, string guestDirectory, CancellationToken ct)
    {
        Interlocked.Increment(ref _lists);
        var prefix = guestDirectory.TrimEnd('/') + "/";
        IReadOnlyList<GuestFileInfo> listed =
        [
            .. _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new GuestFileInfo(f.Key[prefix.Length..], f.Value.Length, Convert.ToHexStringLower(SHA256.HashData(f.Value)))),
        ];
        return Task.FromResult(ListTransform is { } transform ? transform(listed) : listed);
    }

    public Task DeleteDirectoryAsync(Guid vmId, string guestDirectory, CancellationToken ct)
    {
        var prefix = guestDirectory.TrimEnd('/') + "/";
        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
