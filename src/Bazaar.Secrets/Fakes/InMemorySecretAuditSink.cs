namespace Bazaar.Secrets.Fakes;

/// <summary>In-memory audit sink. <see cref="FailWrites"/> simulates an audit outage so tests can prove audit-first.</summary>
public sealed class InMemorySecretAuditSink : ISecretAuditSink
{
    private readonly Lock _gate = new();
    private readonly List<SecretAuditEvent> _events = [];
    private bool _failWrites;

    public bool FailWrites
    {
        get
        {
            lock (_gate)
            {
                return _failWrites;
            }
        }
        set
        {
            lock (_gate)
            {
                _failWrites = value;
            }
        }
    }

    public IReadOnlyList<SecretAuditEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public Task RecordAsync(SecretAuditEvent auditEvent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_failWrites)
            {
                throw new IOException("Audit store unavailable (test fake).");
            }

            _events.Add(auditEvent);
        }

        return Task.CompletedTask;
    }
}
