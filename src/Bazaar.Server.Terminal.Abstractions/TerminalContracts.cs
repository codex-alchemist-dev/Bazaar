using System.Threading.Channels;

namespace Bazaar.Server.Terminal.Abstractions;

/// <summary>Who is asking. The caller identity comes from the authenticated connection, never from a frame (doc 55, section 4).</summary>
/// <param name="MemberId">Human member or agent id.</param>
/// <param name="IsOwner">The owner reads every terminal.</param>
/// <param name="IsAgent">True for an agent session.</param>
public sealed record TerminalCaller(string MemberId, bool IsOwner, bool IsAgent);

/// <summary>One terminal as the broker tracks it.</summary>
public sealed record TerminalInfo(string TerminalId, string? AgentId, string VmId, int Cols, int Rows, string? WriterMemberId, long LastSeq);

/// <summary>Output delivered to a subscriber. <paramref name="IsReplay"/> marks ring replay.</summary>
public readonly record struct TerminalOutputChunk(long Seq, ReadOnlyMemory<byte> Bytes, bool IsReplay);

/// <summary>The seam to the VM module: speaks the guest <c>Pty*</c> messages. The terminal module never references the VM module.</summary>
public interface IGuestPtyChannel
{
    /// <summary>True when a VM backend is registered. The broker fails closed when false.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens a terminal in the VM for the agent and returns its output and exit.</summary>
    Task<IGuestPty> OpenAsync(string vmId, string terminalId, int cols, int rows, CancellationToken ct);

    /// <summary>
    /// Opens a terminal in the VM on behalf of <paramref name="agentId"/>. A VM-backed channel must refuse a VM that is not that agent's
    /// (doc 55, section 4): the broker always calls this overload. The default ignores the agent and calls <see cref="OpenAsync"/>, so a
    /// channel with no notion of ownership (a fake, a local test shell) keeps working.
    /// </summary>
    Task<IGuestPty> OpenForAgentAsync(string agentId, string vmId, string terminalId, int cols, int rows, CancellationToken ct) =>
        OpenAsync(vmId, terminalId, cols, rows, ct);
}

/// <summary>One open guest terminal.</summary>
public interface IGuestPty : IAsyncDisposable
{
    /// <summary>Output in order. Completes when the shell ends.</summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>Exit code. Completes when the shell ends.</summary>
    Task<int> Exited { get; }

    /// <summary>Writes input.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Resizes.</summary>
    ValueTask ResizeAsync(int cols, int rows, CancellationToken ct);
}

/// <summary>Decides who may do what to a terminal. Fails closed.</summary>
public interface ITerminalAccessPolicy
{
    /// <summary>May the caller view the terminal.</summary>
    bool CanView(TerminalCaller caller, TerminalInfo terminal);

    /// <summary>May the caller write input (be the writer).</summary>
    bool CanWrite(TerminalCaller caller, TerminalInfo terminal);
}

/// <summary>Audit sink for control hand-overs and human input (doc 55, section 4).</summary>
public interface ITerminalAudit
{
    /// <summary>Records an event such as <c>open</c>, <c>close</c>, <c>control.take</c>, <c>control.release</c>, <c>input.human</c>.</summary>
    ValueTask RecordAsync(string kind, TerminalCaller caller, string terminalId, CancellationToken ct);
}

/// <summary>Why a broker operation was refused.</summary>
public enum TerminalRefusal
{
    None,
    NoBackend,
    NotFound,
    Forbidden,
    WriterHeld,
    LimitReached,
    InvalidSize,
    RateLimited,
}

/// <summary>Result of a broker operation.</summary>
public readonly record struct TerminalResult(TerminalRefusal Refusal, string? Detail = null)
{
    /// <summary>True when the operation was accepted.</summary>
    public bool Ok => Refusal == TerminalRefusal.None;

    /// <summary>Accepted.</summary>
    public static TerminalResult Accepted { get; } = new(TerminalRefusal.None);
}

/// <summary>A viewer's subscription. Disposing detaches.</summary>
public interface ITerminalSubscription : IAsyncDisposable
{
    /// <summary>Output for this viewer. Replay chunks come first. A dropped (slow) viewer sees a gap in <c>Seq</c> and replays.</summary>
    ChannelReader<TerminalOutputChunk> Output { get; }

    /// <summary>Completes with the exit code when the shell ends.</summary>
    Task<int> Exited { get; }
}

/// <summary>Creates, tracks and fans out agent terminals (doc 55, section 4).</summary>
public interface ITerminalBroker
{
    /// <summary>Opens a terminal for an agent in its VM.</summary>
    Task<(TerminalResult Result, TerminalInfo? Terminal)> OpenAsync(TerminalCaller caller, string agentId, string vmId, int cols, int rows, CancellationToken ct);

    /// <summary>Lists the terminals the caller may see.</summary>
    IReadOnlyList<TerminalInfo> List(TerminalCaller caller);

    /// <summary>Subscribes the caller. <paramref name="fromSeq"/> of 0 means from the oldest ring entry.</summary>
    Task<(TerminalResult Result, ITerminalSubscription? Subscription)> SubscribeAsync(TerminalCaller caller, string terminalId, long fromSeq, CancellationToken ct);

    /// <summary>Writes input. The caller must be the writer.</summary>
    Task<TerminalResult> WriteAsync(TerminalCaller caller, string terminalId, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Resizes. The caller must be the writer.</summary>
    Task<TerminalResult> ResizeAsync(TerminalCaller caller, string terminalId, int cols, int rows, CancellationToken ct);

    /// <summary>Takes the writer role. Audited.</summary>
    Task<TerminalResult> TakeControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct);

    /// <summary>Releases the writer role. Audited.</summary>
    Task<TerminalResult> ReleaseControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct);

    /// <summary>Closes the terminal.</summary>
    Task<TerminalResult> CloseAsync(TerminalCaller caller, string terminalId, CancellationToken ct);
}
