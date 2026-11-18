namespace Bazaar.Contracts.Terminal;

/// <summary>Base of every terminal frame on the <c>pane:&lt;terminalId&gt;</c> stream (doc 52, section 6).</summary>
public abstract record TerminalFrame(string TerminalId);

/// <summary>Client asks the server to open (or attach to) a terminal. <paramref name="AgentId"/> is null for a human-owned shell.</summary>
public sealed record TerminalOpen(string TerminalId, string? AgentId, int Cols, int Rows) : TerminalFrame(TerminalId);

/// <summary>Server accepted the open. <paramref name="ReplayFrom"/> is the first sequence the ring can still replay.</summary>
public sealed record TerminalOpened(string TerminalId, bool WriterHeld, long ReplayFrom) : TerminalFrame(TerminalId);

/// <summary>Terminal output. <paramref name="Seq"/> starts at 1 per terminal and never repeats.</summary>
public sealed record TerminalOutput(string TerminalId, long Seq, byte[] Bytes) : TerminalFrame(TerminalId);

/// <summary>Terminal input from the writer.</summary>
public sealed record TerminalInput(string TerminalId, byte[] Bytes) : TerminalFrame(TerminalId);

/// <summary>Terminal size change from the writer.</summary>
public sealed record TerminalResize(string TerminalId, int Cols, int Rows) : TerminalFrame(TerminalId);

/// <summary>Control hand-over: <c>take</c>, <c>release</c> (client to server), <c>granted</c>, <c>denied</c> (server to client).</summary>
public sealed record TerminalControl(string TerminalId, string Action) : TerminalFrame(TerminalId);

/// <summary>Client asks the server to replay the ring from <paramref name="FromSeq"/>.</summary>
public sealed record TerminalReplay(string TerminalId, long FromSeq) : TerminalFrame(TerminalId);

/// <summary>The shell ended.</summary>
public sealed record TerminalExit(string TerminalId, int Code) : TerminalFrame(TerminalId);

/// <summary>Client closes (detaches from) the terminal.</summary>
public sealed record TerminalClose(string TerminalId) : TerminalFrame(TerminalId);

/// <summary>Server refused or failed an operation.</summary>
public sealed record TerminalError(string TerminalId, string Code, string Message) : TerminalFrame(TerminalId);
