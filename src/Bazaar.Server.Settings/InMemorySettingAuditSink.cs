namespace Bazaar.Server.Settings;

/// <summary>Keeps audit entries in memory, in arrival order. For tests and tools; not durable.</summary>
public sealed class InMemorySettingAuditSink : ISettingAuditSink
{
    private readonly List<SettingChangeAudit> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<SettingChangeAudit> Entries
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    public Task AppendAsync(SettingChangeAudit change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _entries.Add(change);
        return Task.CompletedTask;
    }
}
