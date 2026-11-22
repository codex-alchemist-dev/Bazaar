namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>
/// An explicit, scoped switch that turns read audit off for audited commands on the current async flow. The audit log's own
/// queries use it so recording a read can never record another read (doc 48 section 5.1). The recorder also holds it while it
/// writes a row, so a log that reads through an audited data source cannot recurse. Writes are never audited either way.
/// </summary>
public static class ReadAuditBypass
{
    private static readonly AsyncLocal<int> _depth = new();

    /// <summary>True while a bypass is open on this async flow.</summary>
    public static bool IsActive => _depth.Value > 0;

    /// <summary>Opens a bypass. Dispose it to end it; bypasses nest.</summary>
    public static IDisposable Suppress()
    {
        _depth.Value++;
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            // Runs on the caller's flow (a using block), so it restores the value the caller sees.
            _depth.Value--;
        }
    }
}
