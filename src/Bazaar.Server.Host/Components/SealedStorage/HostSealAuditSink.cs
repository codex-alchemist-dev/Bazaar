using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.SealedStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Components.SealedStorage;

/// <summary>
/// The host's <see cref="ISealAuditSink"/> (doc 61): every sealed-storage event is logged, and appended to the audit log (doc 15) when the
/// <c>audit</c> component is enabled. The audit log is resolved on use, not at construction, so the sealed components start in any order
/// relative to it and a host with <c>audit</c> disabled still records to the log. A failure to append is logged and swallowed: the store
/// that raised a tamper event still throws, and a down audit database must not hide it or turn a read into a different error.
/// </summary>
internal sealed partial class HostSealAuditSink(IServiceProvider services, ILogger<HostSealAuditSink> logger) : ISealAuditSink
{
    private const string _actor = "system:sealed-storage";

    public async ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        LogEvent(auditEvent.Kind, auditEvent.Scope, auditEvent.Path, auditEvent.Detail);
        if (services.GetService<IAuditLog>() is not { } audit)
        {
            return;
        }

        var payload = new JsonObject { ["scope"] = auditEvent.Scope, ["detail"] = auditEvent.Detail };
        if (auditEvent.Path is not null)
        {
            payload["path"] = auditEvent.Path;
        }

        try
        {
            await audit.AppendAsync(new AuditEntry(auditEvent.At, _actor, auditEvent.Kind, payload.ToJsonString()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAppendFailed(ex, auditEvent.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sealed storage event {Kind} scope {Scope} path {Path}: {Detail}")]
    private partial void LogEvent(string kind, string scope, string? path, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not append sealed storage event {Kind} to the audit log.")]
    private partial void LogAppendFailed(Exception exception, string kind);
}
