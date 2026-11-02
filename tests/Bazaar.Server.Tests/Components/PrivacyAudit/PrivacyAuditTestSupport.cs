using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Tests.Components.PrivacyAudit;

/// <summary>In-memory <see cref="IAuditLog"/> that records every entry in arrival order.</summary>
internal sealed class FakeAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries => _entries;

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _entries.Add(e);
        return Task.CompletedTask;
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock is set by the test.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
