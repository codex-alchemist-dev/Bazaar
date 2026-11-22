using Bazaar.Server.Settings;

namespace Bazaar.Server.Setup.Host;

/// <summary>Adapts the server's <see cref="ISettingAuditSink"/> to <see cref="ISetupSettingAudit"/>: each plan change is one accepted install-scope row.</summary>
public sealed class SettingAuditSinkSetupAudit(ISettingAuditSink sink) : ISetupSettingAudit
{
    private readonly ISettingAuditSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <inheritdoc />
    public Task AppendInstallChangeAsync(string key, string actorId, string? oldValueJson, string newValueJson, CancellationToken cancellationToken = default) =>
        _sink.AppendAsync(
            new SettingChangeAudit(key, SettingScope.Install, string.Empty, actorId, SettingWriteOutcome.Accepted, oldValueJson, newValueJson, null),
            cancellationToken);
}
