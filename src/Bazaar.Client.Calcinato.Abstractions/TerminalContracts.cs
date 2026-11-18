using System.Threading.Channels;

namespace Bazaar.Client.Calcinato.Abstractions;

/// <summary>What to start in a local terminal (doc 52, section 5.3).</summary>
/// <param name="Shell">Shell path or name. Null picks the default by the order in doc 52.</param>
/// <param name="Cols">Initial columns.</param>
/// <param name="Rows">Initial rows.</param>
/// <param name="WorkingDirectory">Start directory. Null means the user's home.</param>
/// <param name="Environment">Extra environment. Names on the deny list are dropped by the backend.</param>
public sealed record TerminalSpec(
    string? Shell,
    int Cols,
    int Rows,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Starts local PTY sessions. Implemented only by <c>Bazaar.Client.Calcinato.Pty</c> and by test fakes.</summary>
public interface IPtyBackend
{
    /// <summary>True when this backend can run on the current OS.</summary>
    bool IsSupported { get; }

    /// <summary>Starts a shell under a pseudo console.</summary>
    IPtySession Open(TerminalSpec spec);
}

/// <summary>One local PTY session. Disposing ends the shell and its process tree.</summary>
public interface IPtySession : IAsyncDisposable
{
    /// <summary>Terminal output in order. Completes when the shell ends.</summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>The shell's exit code. Completes when the shell ends and never faults.</summary>
    Task<int> Exited { get; }

    /// <summary>Writes terminal input.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Changes the terminal size.</summary>
    void Resize(int cols, int rows);
}

/// <summary>
/// A byte stream the emulator reads and writes: a local PTY or a server terminal. The engine does not know which
/// (doc 52, section 5.3).
/// </summary>
public interface ITerminalSource : IAsyncDisposable
{
    /// <summary>True for a server terminal.</summary>
    bool IsRemote { get; }

    /// <summary>False for a read-only viewer. Insert mode is refused then.</summary>
    bool CanWrite { get; }

    /// <summary>Output in order. Completes when the terminal ends.</summary>
    ChannelReader<TerminalChunk> Output { get; }

    /// <summary>Exit code, completed when the terminal ends.</summary>
    Task<int> Exited { get; }

    /// <summary>Writes input bytes.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Changes the size.</summary>
    ValueTask ResizeAsync(int cols, int rows, CancellationToken ct);
}

/// <summary>One chunk of output. <paramref name="Seq"/> is 0 for local sources and the server sequence for remote ones.</summary>
/// <param name="Seq">Sequence number, or 0.</param>
/// <param name="Bytes">Raw terminal bytes.</param>
/// <param name="IsReplay">True when the chunk comes from the scrollback ring.</param>
public readonly record struct TerminalChunk(long Seq, ReadOnlyMemory<byte> Bytes, bool IsReplay = false);

/// <summary>Supplies a head's PTY backend and opens terminals. The head implements it (doc 27, <c>IPlatformServices.Terminal</c>).</summary>
public interface ITerminalHost
{
    /// <summary>True when a local shell can run on this head. Android is false in v1.</summary>
    bool SupportsLocal { get; }

    /// <summary>Opens a local terminal source.</summary>
    ITerminalSource OpenLocal(TerminalSpec spec);

    /// <summary>Attaches to a server terminal.</summary>
    Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct);
}

/// <summary>Inline suggestions. The default source returns nothing. The NPU source is off unless the person turns it on (doc 41, section 4.11).</summary>
public interface ISuggestionSource
{
    /// <summary>Returns ghost text for the current input line, or null. Never runs a command.</summary>
    ValueTask<string?> SuggestAsync(string inputLine, CancellationToken ct);
}
