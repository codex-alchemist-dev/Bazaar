using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Community.Tests.Actions;

/// <summary>In-memory <see cref="IAuditLog"/> that records every entry. Set <see cref="FailNext"/> to make the next append throw.</summary>
internal sealed class FakeAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public bool FailNext { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("audit write failed");
        }
        Entries.Add(e);
        return Task.CompletedTask;
    }
}
