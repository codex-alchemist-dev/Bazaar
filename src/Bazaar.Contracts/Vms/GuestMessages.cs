using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Vms;

/// <summary>Base of every guest protocol message. The concrete record's type decides the wire tag.</summary>
public abstract record GuestMessage;

/// <summary>Output stream of an exec or a pty.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputSource>))]
public enum OutputSource
{
    /// <summary>Standard output.</summary>
    Stdout,

    /// <summary>Standard error.</summary>
    Stderr,
}

/// <summary>First message from the guest after it starts; the host checks the nonce and the VM id.</summary>
/// <param name="AgentVersion">Guest agent build string.</param>
/// <param name="VmId">VM id the guest was provisioned with.</param>
/// <param name="Nonce">Host-supplied nonce echoed back so the host can reject a replayed hello.</param>
/// <param name="ProtocolVersion">
/// Highest guest protocol version the agent speaks (<see cref="GuestFrameCodec.Version"/>). Older agents do not send it,
/// so it reads as 0, and the host then treats the agent as predating version 3.
/// </param>
public sealed record Hello(string AgentVersion, string VmId, string Nonce, int ProtocolVersion = 0) : GuestMessage;

/// <summary>Host request to run a process in the guest.</summary>
/// <param name="Id">Exec id, unique per VM.</param>
/// <param name="Argv">Program and arguments; no shell is implied.</param>
/// <param name="Cwd">Working directory in the guest.</param>
/// <param name="EnvAllowlist">Names of host-provided variables the guest may pass through.</param>
/// <param name="TimeoutSec">Seconds before the whole process tree is killed.</param>
public sealed record Exec(string Id, IReadOnlyList<string> Argv, string Cwd, IReadOnlyList<string> EnvAllowlist, int TimeoutSec) : GuestMessage;

/// <summary>A chunk of an exec's output, in order per stream by <paramref name="Seq"/>.</summary>
/// <param name="Id">Exec id.</param>
/// <param name="Stream">Stream the bytes came from.</param>
/// <param name="Seq">Chunk sequence number, starting at 0 per stream.</param>
/// <param name="Bytes">Raw output bytes; base64 in JSON.</param>
public sealed record Output(string Id, OutputSource Stream, long Seq, byte[] Bytes) : GuestMessage;

/// <summary>Host request to stop an exec.</summary>
/// <param name="Id">Exec id.</param>
public sealed record Cancel(string Id) : GuestMessage;

/// <summary>Final message for an exec.</summary>
/// <param name="Id">Exec id.</param>
/// <param name="Code">Process exit code; -1 when the process did not exit normally.</param>
/// <param name="TimedOut">True when the timeout killed the process tree.</param>
[SuppressMessage("Naming", "CA1716", Justification = "Spec name for the exit message; the wire tag is exit.")]
public sealed record Exit(string Id, int Code, bool TimedOut) : GuestMessage;

/// <summary>Host announces a file upload; the content bytes follow the frame.</summary>
/// <param name="Path">Guest path, checked against the VM's mount list.</param>
/// <param name="Size">Byte count of the content that follows.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the content.</param>
public sealed record PutFile(string Path, long Size, string Sha256) : GuestMessage;

/// <summary>Host asks for a file; the guest answers with a <see cref="PutFile"/> header and its bytes.</summary>
/// <param name="Path">Guest path, checked against the VM's mount list.</param>
public sealed record GetFile(string Path) : GuestMessage;

/// <summary>Liveness probe in either direction.</summary>
public sealed record Heartbeat : GuestMessage;

/// <summary>Host opens an agent terminal in the guest.</summary>
/// <param name="Id">Pty id, unique per VM.</param>
/// <param name="Cols">Terminal width in cells.</param>
/// <param name="Rows">Terminal height in cells.</param>
public sealed record PtyOpen(string Id, int Cols, int Rows) : GuestMessage;

/// <summary>Terminal bytes in either direction.</summary>
/// <param name="Id">Pty id.</param>
/// <param name="Seq">Chunk sequence number, starting at 0 per direction.</param>
/// <param name="Bytes">Raw terminal bytes; base64 in JSON.</param>
public sealed record PtyData(string Id, long Seq, byte[] Bytes) : GuestMessage;

/// <summary>Host changes the terminal size.</summary>
/// <param name="Id">Pty id.</param>
/// <param name="Cols">Terminal width in cells.</param>
/// <param name="Rows">Terminal height in cells.</param>
public sealed record PtyResize(string Id, int Cols, int Rows) : GuestMessage;

/// <summary>Closes a terminal.</summary>
/// <param name="Id">Pty id.</param>
public sealed record PtyClose(string Id) : GuestMessage;

/// <summary>
/// Guest answer to a <see cref="PutFile"/>, sent once the upload succeeds or fails. It follows the file content, so a
/// host that waits for it knows the content was read.
/// </summary>
/// <param name="Path">Guest path from the PutFile.</param>
/// <param name="Ok">True when the file was written and moved into place.</param>
/// <param name="Error">Why the upload failed; null when <paramref name="Ok"/> is true.</param>
public sealed record PutFileResult(string Path, bool Ok, string? Error) : GuestMessage;

/// <summary>
/// Guest answer to a <see cref="GetFile"/> that cannot be served: the path is refused, or the file is missing or cannot
/// be read. Sent in place of the PutFile header and bytes, so the host does not wait forever.
/// </summary>
/// <param name="Path">Guest path from the GetFile.</param>
/// <param name="Error">Why the file was not sent.</param>
public sealed record GetFileError(string Path, string Error) : GuestMessage;

/// <summary>Sent when a terminal's shell exits on its own. A terminal the host closes sends no PtyExit.</summary>
/// <param name="Id">Pty id.</param>
/// <param name="Code">Shell exit code.</param>
public sealed record PtyExit(string Id, int Code) : GuestMessage;

/// <summary>
/// Sent when an exec is refused before it starts. The guest always follows it with <see cref="Exit"/> for the same id
/// and code -1, so consumers written before this message still see the exec end. A host that knows ExecError can
/// ignore that Exit for a refused exec.
/// </summary>
/// <param name="Id">Exec id from the refused Exec.</param>
/// <param name="Code">Machine-readable reason; one of the values in <see cref="ExecRefusalCodes"/>.</param>
/// <param name="Message">Human-readable reason.</param>
public sealed record ExecError(string Id, string Code, string Message) : GuestMessage;

/// <summary>
/// Host asks the guest to delete one file (protocol version 3). The path is relative to the worktree root; the guest
/// rejects absolute paths and <c>..</c>. A symlink is removed itself, never its target, and a directory is refused.
/// </summary>
/// <param name="Path">Root-relative path of the file.</param>
public sealed record DeleteFile(string Path) : GuestMessage;

/// <summary>Guest answer to a <see cref="DeleteFile"/>.</summary>
/// <param name="Path">Path from the DeleteFile.</param>
/// <param name="Ok">True when the file was removed.</param>
/// <param name="Error">Why it was not; null when <paramref name="Ok"/> is true.</param>
public sealed record DeleteFileResult(string Path, bool Ok, string? Error) : GuestMessage;

/// <summary>Host asks for the entries of one directory, not recursive (protocol version 3). The path is relative to the worktree root.</summary>
/// <param name="Dir">Root-relative directory; empty or <c>.</c> is the root itself.</param>
public sealed record ListFiles(string Dir) : GuestMessage;

/// <summary>One directory entry in a <see cref="ListFilesResult"/>.</summary>
/// <param name="Name">Entry name, no separators.</param>
/// <param name="Size">Byte size of a file; 0 for a directory.</param>
/// <param name="IsDirectory">True for a directory.</param>
public sealed record GuestFileEntry(string Name, long Size, bool IsDirectory);

/// <summary>Guest answer to a <see cref="ListFiles"/>.</summary>
/// <param name="Dir">Directory from the ListFiles.</param>
/// <param name="Entries">Entries ordered by name; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the directory could not be listed; null on success.</param>
public sealed record ListFilesResult(string Dir, IReadOnlyList<GuestFileEntry> Entries, string? Error) : GuestMessage;

/// <summary>
/// Host asks where a path really points once symlinks are followed (protocol version 3). The path is relative to the
/// worktree root.
/// </summary>
/// <param name="Path">Root-relative path.</param>
public sealed record ResolveRealPath(string Path) : GuestMessage;

/// <summary>Guest answer to a <see cref="ResolveRealPath"/>.</summary>
/// <param name="Path">Path from the ResolveRealPath.</param>
/// <param name="RealPath">
/// Root-relative real path with <c>/</c> separators, <c>.</c> for the root itself. Null when the target leaves the
/// root, cannot be resolved, or the path was refused (fail closed).
/// </param>
/// <param name="Error">Set only when the path was refused outright (absolute or <c>..</c>); null otherwise.</param>
public sealed record ResolveRealPathResult(string Path, string? RealPath, string? Error) : GuestMessage;

/// <summary>Reason codes carried in <see cref="ExecError.Code"/>.</summary>
public static class ExecRefusalCodes
{
    /// <summary>The exec id is empty.</summary>
    public const string EmptyId = "empty-id";

    /// <summary>The argv is empty or its program name is empty.</summary>
    public const string NoProgram = "no-program";

    /// <summary>The timeout is zero or negative.</summary>
    public const string TimeoutNotPositive = "timeout-not-positive";

    /// <summary>An exec with the same id is already running.</summary>
    public const string DuplicateId = "duplicate-id";

    /// <summary>The concurrency cap is reached.</summary>
    public const string TooManyExecs = "too-many-execs";

    /// <summary>The working directory is empty or not inside an allowed root. Also used when no roots are configured.</summary>
    public const string CwdNotAllowed = "cwd-not-allowed";

    /// <summary>The program could not be started, for example because it does not exist.</summary>
    public const string ProgramNotStarted = "program-not-started";
}
