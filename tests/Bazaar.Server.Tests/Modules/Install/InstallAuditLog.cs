using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Tests.Modules.Install;

/// <summary>An <see cref="IAuditLog"/> that keeps entries in memory, so a test can see what was recorded and what was not.</summary>
internal sealed class InstallAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}
