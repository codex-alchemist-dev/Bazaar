namespace Bazaar.Client.Athanor.Gtk;

/// <summary>The life of the proxy (doc 57 section 2.1).</summary>
public enum EditorHelperState
{
    /// <summary>Not started yet.</summary>
    Stopped,

    /// <summary>A helper is starting and has not finished the handshake.</summary>
    Starting,

    /// <summary>A helper is up and the page is ready.</summary>
    Running,

    /// <summary>The helper died and the proxy is waiting to restart it.</summary>
    Backoff,

    /// <summary>Too many failures in a row. Stays down until <see cref="GtkEditorHost.RestartAsync"/>.</summary>
    Faulted,

    /// <summary>Disposed.</summary>
    Disposed,
}

/// <summary>Where and how big the helper's editor window should be. Position is a hint on Wayland (doc 57 section 2.1).</summary>
public sealed record EditorWindowPlacement(int X, int Y, int Width, int Height, bool Visible, string? Title = null);

/// <summary>The helper cannot be used (it never started, or the host is faulted or disposed).</summary>
public sealed class EditorHelperException : Exception
{
    public EditorHelperException(string message)
        : base(message)
    {
    }

    public EditorHelperException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One running helper, as lines of text. The real one wraps a process; tests use an in-process pair.</summary>
public interface IEditorHelperConnection : IAsyncDisposable
{
    /// <summary>Writes one line (no newline in it) to the helper.</summary>
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Reads one line from the helper, or null at end of output. May throw <see cref="InvalidDataException"/> for an oversize line.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Completes with the exit code when the helper is gone.</summary>
    Task<int> Exited { get; }

    /// <summary>The last lines the helper wrote to standard error, for diagnostics. Null when there are none.</summary>
    string? DiagnosticTail { get; }

    /// <summary>Ends the helper now. Safe to call more than once.</summary>
    void Kill();
}

/// <summary>Starts helpers. One call, one new helper.</summary>
public interface IEditorHelperLauncher
{
    ValueTask<IEditorHelperConnection> LaunchAsync(CancellationToken cancellationToken);
}
