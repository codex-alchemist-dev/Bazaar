using Bazaar.Server.Security.Audit;

namespace Bazaar.Admin.Placeholders;

/// <summary>
/// PLACEHOLDER. Discards audit rows. The CLI has no audit store yet (doc 15 audit_log on Postgres); the Host adapter replaces this
/// class. Until then an apply writes no durable setup.plan.applied row from this command, and the help text says so.
/// </summary>
internal sealed class NullSetupAuditLog : IAuditLog
{
    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
