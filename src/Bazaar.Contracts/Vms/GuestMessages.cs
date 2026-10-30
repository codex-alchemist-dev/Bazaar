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
public sealed record Hello(string AgentVersion, string VmId, string Nonce) : GuestMessage;

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
