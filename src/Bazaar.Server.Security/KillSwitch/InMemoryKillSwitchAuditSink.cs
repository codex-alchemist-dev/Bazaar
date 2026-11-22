namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Process-local <see cref="IKillSwitchAuditSink"/> for tests and single-process use. Thread-safe. Records are kept in
/// arrival order and are never removed.
/// </summary>
public sealed class InMemoryKillSwitchAuditSink : IKillSwitchAuditSink
{
    private readonly Lock _gate = new();
    private readonly List<KillSwitchAuditEntry> _entries = [];

    /// <summary>Every record received so far, in arrival order.</summary>
    public IReadOnlyList<KillSwitchAuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }
}
