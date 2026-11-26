namespace Bazaar.Server.VmTransport;

/// <summary>
/// Limits of one guest channel. Constructor options for now: the <c>vm.exec.*</c> and heartbeat settings feed them once settings wiring lands.
/// Defaults follow doc 16: heartbeat every 30 s, lost after 3 missed intervals.
/// </summary>
public sealed record GuestChannelOptions
{
    /// <summary>Gap between host heartbeats, and the length of one interval the watchdog counts.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Intervals without any guest frame after which the channel is Lost.</summary>
    public int MissedHeartbeatLimit { get; init; } = 3;

    /// <summary>How long the guest has to send its Hello after connecting.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Output bytes kept per exec (<c>vm.exec.output_cap_mb</c>). Output beyond it is dropped and the result is flagged.</summary>
    public long MaxOutputBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Longest exec timeout a caller may ask for. A longer request is refused with an exception.</summary>
    public TimeSpan MaxExecTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long past an exec's own timeout the host waits for the guest's Exit before it asks the guest to cancel.</summary>
    public TimeSpan TimeoutGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the host waits for the Exit after a cancel before it gives up on the exec.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Execs that may run at once on one channel. The guest enforces its own cap too.</summary>
    public int MaxConcurrentExecs { get; init; } = 4;

    /// <summary>Largest file moved in either direction.</summary>
    public long MaxFileBytes { get; init; } = 1L << 30;

    /// <summary>Terminal output bytes kept for a reader that falls behind. A terminal that passes it is ended.</summary>
    public long MaxPendingPtyBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>Terminals that may be open on one channel at once.</summary>
    public int MaxOpenPtys { get; init; } = 8;

    /// <summary>
    /// Longest one frame or one slice of file content may take to write. A guest that stops reading fills the transport buffer and the write stalls;
    /// past this the channel is faulted and its stream closed, so one stuck guest cannot wedge the host.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the guest has to answer a file request (the PutFileResult, or the start of the content for a read). Past it the channel is faulted.
    /// Once content is arriving, stalls are caught by the heartbeat watchdog instead.
    /// </summary>
    public TimeSpan FileAnswerTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Longest the content of one file read may take once it has started to arrive. Every byte the guest sends resets the heartbeat watchdog, so a guest
    /// that trickles content would otherwise hold the file gate for ever. Past it the channel is faulted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan FileTransferTimeout
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromMinutes(30);
}

/// <summary>What the first frame of a connection must carry.</summary>
/// <param name="VmId">The VM id the host provisioned the guest for. The Hello must echo it.</param>
/// <param name="Nonce">The nonce the server issued at create. The Hello must echo it.</param>
public sealed record GuestHandshake(string VmId, string Nonce);

/// <summary>Where a channel is in its life.</summary>
public enum GuestChannelState
{
    /// <summary>Hello accepted; frames flow.</summary>
    Open,

    /// <summary>Ended in order: the guest closed it, or the host disposed it.</summary>
    Closed,

    /// <summary>Heartbeats were missed for the allowed number of intervals. Open execs are reported lost.</summary>
    Lost,

    /// <summary>Ended on a protocol error, an unknown frame, or a broken stream.</summary>
    Faulted,
}

/// <summary>The first frame was not an acceptable Hello. The connection is closed.</summary>
public sealed class GuestHandshakeException : GuestChannelException
{
    /// <summary>Creates the exception.</summary>
    public GuestHandshakeException(string message) : base(message)
    {
    }
}
