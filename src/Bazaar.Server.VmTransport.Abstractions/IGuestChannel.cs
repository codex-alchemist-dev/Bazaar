namespace Bazaar.Server.VmTransport;

/// <summary>A process to run in the guest. Mirrors the provisional shape in the coding workspace ports (doc 16 guest protocol).</summary>
/// <param name="Argv">Program and arguments. The first element is the program.</param>
/// <param name="Cwd">Working directory in the guest, absolute.</param>
/// <param name="Env">
/// Environment variables to pass. The guest wire carries variable names only (<c>envAllowlist</c>), so the channel sends the keys and the guest
/// passes its own values for them. The values here never leave the host.
/// </param>
/// <param name="Timeout">The guest kills the whole process tree when this elapses.</param>
public sealed record ExecRequest(
    IReadOnlyList<string> Argv,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    TimeSpan Timeout);

/// <summary>One piece of streamed output from a running process.</summary>
/// <param name="Seq">Position in the process output, starting at 0.</param>
/// <param name="Stream">Either "stdout" or "stderr".</param>
/// <param name="Text">The output text.</param>
public sealed record OutputChunk(long Seq, string Stream, string Text);

/// <summary>How a process ended.</summary>
/// <param name="ExitCode">The process exit code, or -1 when none was reported.</param>
/// <param name="TimedOut">True when the process was stopped for exceeding its timeout.</param>
/// <param name="Cancelled">True when the process was stopped by a cancellation request.</param>
/// <param name="OutputTruncated">True when output beyond the channel's output cap was dropped. PROPOSED addition to the provisional shape.</param>
public sealed record ExecResult(int ExitCode, bool TimedOut, bool Cancelled, bool OutputTruncated = false)
{
    /// <summary>True only for a zero exit with no timeout and no cancellation.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>
/// The guest wire as the host sees it (doc 16 IGuestChannel). Paths are guest paths. Callers must pass jailed paths.
/// A channel that ends (guest closed, heartbeats lost, protocol error) fails every call in flight with <see cref="GuestChannelException"/>.
/// </summary>
public interface IGuestChannel
{
    /// <summary>
    /// Runs a process. <paramref name="onOutput"/> receives chunks in order as they arrive. Cancelling <paramref name="ct"/> asks the guest to kill
    /// the process tree, waits for its exit, and returns a result with <see cref="ExecResult.Cancelled"/> set.
    /// </summary>
    /// <exception cref="GuestExecRefusedException">The guest refused the exec.</exception>
    /// <exception cref="GuestChannelException">The channel ended before the process did.</exception>
    Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct);

    /// <summary>
    /// Writes a file. The guest rejects content whose SHA-256 (lower-case hex) does not match <paramref name="sha256"/>. The content stream must
    /// report its length (a seekable stream), because the wire announces the size first.
    /// </summary>
    /// <exception cref="GuestFileException">The guest refused or failed the write.</exception>
    Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct);

    /// <summary>
    /// Copies a file from the guest into <paramref name="sink"/>. The bytes are hash-checked as they arrive. On a mismatch the call fails, and the
    /// caller must discard what the sink received.
    /// </summary>
    /// <exception cref="GuestFileException">The guest refused or failed the read, or the hash did not match.</exception>
    Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct);

    /// <summary>Opens a terminal in the guest. The wire has no acknowledgement, so a shell that fails to start shows as a terminal with no output.</summary>
    Task<IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct);
}

/// <summary>One terminal in the guest (doc 16 PtyOpen, PtyData, PtyResize, PtyClose).</summary>
public interface IGuestPty : IAsyncDisposable
{
    /// <summary>The terminal id on the wire.</summary>
    string Id { get; }

    /// <summary>Bytes the terminal produced, in order. Completes when the terminal ends.</summary>
    System.Threading.Channels.ChannelReader<byte[]> Output { get; }

    /// <summary>Completes with the shell's exit code, or -1 when the host closed the terminal or the channel ended first.</summary>
    Task<int> Exited { get; }

    /// <summary>Sends keystrokes or pasted bytes to the terminal.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Tells the terminal its new size.</summary>
    ValueTask ResizeAsync(int cols, int rows, CancellationToken ct);

    /// <summary>Closes the terminal. Disposing does the same. Safe to call twice.</summary>
    ValueTask CloseAsync();
}

/// <summary>The channel ended or is unusable.</summary>
public class GuestChannelException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GuestChannelException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the failure that ended the channel.</summary>
    public GuestChannelException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The guest refused an exec. <see cref="Code"/> is one of the exec refusal codes of the guest protocol.</summary>
public sealed class GuestExecRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GuestExecRefusedException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Machine-readable refusal reason.</summary>
    public string Code { get; }
}

/// <summary>A file transfer failed on the guest, or its content did not match its hash.</summary>
public sealed class GuestFileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GuestFileException(string message) : base(message)
    {
    }
}
