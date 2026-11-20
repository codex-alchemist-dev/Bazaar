using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive.Tests;

internal sealed class InMemoryGrantStore : IHiveGrantStore
{
    private readonly List<HiveGrant> _grants = [];

    public Task<bool> AddAsync(HiveGrant grant, CancellationToken ct)
    {
        if (_grants.Any(g => Same(g, grant.FromProject, grant.ToProject, grant.Kind)))
            return Task.FromResult(false);
        _grants.Add(grant);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct) =>
        Task.FromResult(_grants.RemoveAll(g => Same(g, source, target, kind)) > 0);

    public Task<bool> ExistsAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct) =>
        Task.FromResult(_grants.Any(g => Same(g, source, target, kind)));

    public Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HiveGrant>>([.. _grants]);

    private static bool Same(HiveGrant g, ProjectId source, ProjectId target, string kind) =>
        g.FromProject.Equals(source) && g.ToProject.Equals(target) && g.Kind == kind;
}

internal sealed class InMemoryCopyStore : IHiveCopyStore
{
    private readonly Dictionary<Guid, HiveCopy> _copies = [];

    public Task<bool> TryInsertAsync(HiveCopy copy, CancellationToken ct)
    {
        if (_copies.Values.Any(c => c.FromProject.Equals(copy.FromProject) && c.OriginMemory == copy.OriginMemory && c.ToProject.Equals(copy.ToProject)))
            return Task.FromResult(false);
        _copies[copy.CopyId] = copy;
        return Task.FromResult(true);
    }

    public Task<HiveCopy?> FindAsync(ProjectId source, string originMemory, ProjectId target, CancellationToken ct) =>
        Task.FromResult(_copies.Values.FirstOrDefault(c => c.FromProject.Equals(source) && c.OriginMemory == originMemory && c.ToProject.Equals(target)));

    public Task<HiveCopy?> GetAsync(Guid copyId, CancellationToken ct) => Task.FromResult(_copies.GetValueOrDefault(copyId));

    public Task<IReadOnlyList<HiveCopy>> ListDueAsync(DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HiveCopy>>(
            [.. _copies.Values.Where(c => c.State == HiveCopyState.InReview && c.ReviewUntil is { } until && until <= now)]);

    public Task<IReadOnlyList<HiveCopy>> ListLiveAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HiveCopy>>(
            [.. _copies.Values.Where(c => c.FromProject.Equals(source) && c.ToProject.Equals(target) && c.Kind == kind && c.State != HiveCopyState.Deprecated)]);

    public Task<IReadOnlyList<HiveCopy>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HiveCopy>>([.. _copies.Values]);

    public Task<bool> MarkActiveAsync(Guid copyId, DateTimeOffset at, CancellationToken ct)
    {
        if (_copies.GetValueOrDefault(copyId) is not { State: HiveCopyState.InReview } c)
            return Task.FromResult(false);
        _copies[copyId] = c with { State = HiveCopyState.Active, ReleasedAt = at };
        return Task.FromResult(true);
    }

    public Task<bool> MarkDeprecatedAsync(Guid copyId, DateTimeOffset at, CancellationToken ct)
    {
        if (_copies.GetValueOrDefault(copyId) is not { } c || c.State == HiveCopyState.Deprecated)
            return Task.FromResult(false);
        _copies[copyId] = c with { State = HiveCopyState.Deprecated, DeprecatedAt = at };
        return Task.FromResult(true);
    }
}

/// <summary>Rejects any text containing one of the banned words; accepts everything else.</summary>
internal sealed class FakeSanitizer(params string[] banned) : IHiveSanitizer
{
    public int Calls { get; private set; }

    public Task<HiveSanitizeResult> CheckAsync(string title, string body, CancellationToken ct)
    {
        Calls++;
        var hit = banned.FirstOrDefault(b => title.Contains(b, StringComparison.OrdinalIgnoreCase) || body.Contains(b, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(hit is null ? HiveSanitizeResult.Ok : HiveSanitizeResult.Reject($"names '{hit}'"));
    }
}

internal sealed class FakeVaultWriter : IHiveVaultWriter
{
    /// <summary>Files by path; front matter edits are applied to the stored text.</summary>
    public Dictionary<string, string> Files { get; } = [];

    public List<string> Operations { get; } = [];

    public Task WriteAsync(string path, string markdown, CancellationToken ct)
    {
        Operations.Add($"write {path}");
        Files[path] = markdown;
        return Task.CompletedTask;
    }

    public Task SetFrontMatterAsync(string path, string key, string value, CancellationToken ct)
    {
        Operations.Add($"set {path} {key}={value}");
        var lines = Files[path].Split('\n').ToList();
        var i = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        lines[i] = $"{key}: {value}";
        Files[path] = string.Join('\n', lines);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAudit : IHiveAudit
{
    public List<HiveAuditEvent> Entries { get; } = [];

    public Task AppendAsync(HiveAuditEvent entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSettings(HiveOptions options) : IHiveSettings
{
    public HiveOptions Current { get; set; } = options;

    public Task<HiveOptions> GetAsync(CancellationToken ct) => Task.FromResult(Current);
}
