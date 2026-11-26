using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Writes one <see cref="AuditLogKekSink.CustodyEventKind"/> audit entry at host start: which provider holds the KEK and how strong
/// that custody is (doc 14). It carries the provider kind and custody class only, never key material. A failed write is logged and
/// does not stop the host, because the audit table may not be migrated yet on a first run.
/// </summary>
public sealed partial class KekCustodyRecorder(
    AuditLogKekSink? sink,
    string? configuredKind,
    ILogger<KekCustodyRecorder> logger) : IHostedService
{
    /// <summary>The custody class for a provider kind: <c>file</c>, <c>os-user</c> (DPAPI), <c>remote</c> (OpenBao) or <c>hardware</c> (TPM).</summary>
    public static string CustodyClassFor(string kind) => kind switch
    {
        "file" => "file",
        "dpapi" => "os-user",
        "openbao" => "remote",
        "tpm" => "hardware",
        _ => "unknown",
    };

    /// <summary>The provider kind in effect: the configured value, trimmed and lower-cased, or the platform default.</summary>
    public static string EffectiveKind(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? KekProviderFactory.DefaultKind : configured.Trim().ToLowerInvariant();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (sink is null)
            return;

        var kind = EffectiveKind(configuredKind);
        try
        {
            await sink.RecordCustodyAsync(kind, CustodyClassFor(kind), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordFailed(logger, ex, kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record KEK custody for provider {Provider} in the audit log.")]
    private static partial void LogRecordFailed(ILogger logger, Exception ex, string provider);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
