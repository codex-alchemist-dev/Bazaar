using System.Text.Json;

namespace Bazaar.Server.Settings;

/// <summary>
/// Runs the doc 15 floor checks on every write of a <c>security.*</c> key before the inner store sees it. A refused write
/// never reaches the inner store, so nothing is stored, and it writes one refused audit entry (for a key in the table).
/// Unknown <c>security.*</c> names are refused and not audited, because there is no key to audit (see
/// <see cref="ISettingAuditSink"/>). Other keys pass straight through.
/// </summary>
/// <remarks>
/// Accepted and held writes are audited by the inner store, under its per-key lock and with the old value, so each
/// one is audited exactly once. Wire the inner store and this decorator to the same <see cref="ISettingAuditSink"/>.
/// A refused entry carries a null old value: the refusal never reads storage.
/// </remarks>
public sealed class FloorCheckingSettingStore(ISettingStore inner, ISettingAuditSink audit) : ISettingStore
{
    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        inner.GetAsync(key, scopeId, cancellationToken);

    public async Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!SecuritySettings.IsSecurityName(key.Name))
            return await inner.SetAsync(key, value, actor, scopeId, cancellationToken).ConfigureAwait(false);

        using var proposed = JsonSerializer.SerializeToDocument(value, SettingJson.Options);
        return await GuardAsync(key.Name, proposed.RootElement, actor, scopeId, ct => inner.SetAsync(key, value, actor, scopeId, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!SecuritySettings.IsSecurityName(key))
            return inner.SetJsonAsync(key, value, actor, scopeId, cancellationToken);

        return GuardAsync(key, value, actor, scopeId, ct => inner.SetJsonAsync(key, value, actor, scopeId, ct), cancellationToken);
    }

    private async Task<SettingWriteResult> GuardAsync(
        string key,
        JsonElement proposed,
        SettingActor actor,
        string scopeId,
        Func<CancellationToken, Task<SettingWriteResult>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scopeId);

        var check = SecurityFloors.Check(key, proposed);
        if (check.Allowed)
            return await write(cancellationToken).ConfigureAwait(false);

        // Unknown names have no key to audit, so only the table's keys get a refused entry.
        if (SecuritySettings.TryFind(key, out var definition) && definition is not null)
        {
            await audit.AppendAsync(
                new SettingChangeAudit(
                    key,
                    definition.Scope,
                    scopeId,
                    actor.Id,
                    SettingWriteOutcome.Rejected,
                    OldValueJson: null,
                    NewValueJson: proposed.GetRawText(),
                    check.Reason),
                cancellationToken).ConfigureAwait(false);
        }

        return new SettingWriteResult(key, SettingWriteOutcome.Rejected, ApprovalTier.None, check.Reason);
    }
}
