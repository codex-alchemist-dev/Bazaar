using Bazaar.Contracts.Terminal;
using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal;

/// <summary>
/// Translates broker events to and from the wire frames of <c>Bazaar.Contracts.Terminal</c> (doc 52, section 6), so the wire host
/// stays a thin layer. Pure and stateless.
/// </summary>
public static class TerminalFrameMapper
{
    /// <summary>Control actions a client may send.</summary>
    public const string ActionTake = "take";

    /// <summary>Control action: release.</summary>
    public const string ActionRelease = "release";

    /// <summary>Control action the server sends when control was granted.</summary>
    public const string ActionGranted = "granted";

    /// <summary>Control action the server sends when control was denied.</summary>
    public const string ActionDenied = "denied";

    /// <summary>The stable error code string of a refusal. <see cref="TerminalRefusal.None"/> maps to an empty string.</summary>
    public static string ErrorCode(TerminalRefusal refusal) => refusal switch
    {
        TerminalRefusal.None => string.Empty,
        TerminalRefusal.NoBackend => "terminal.no_backend",
        TerminalRefusal.NotFound => "terminal.not_found",
        TerminalRefusal.Forbidden => "terminal.forbidden",
        TerminalRefusal.WriterHeld => "terminal.writer_held",
        TerminalRefusal.LimitReached => "terminal.limit_reached",
        TerminalRefusal.InvalidSize => "terminal.invalid_size",
        TerminalRefusal.RateLimited => "terminal.rate_limited",
        _ => "terminal.error",
    };

    /// <summary>Parses an error code back to a refusal. An unknown code is <c>null</c>.</summary>
    public static TerminalRefusal? RefusalFromCode(string? code) => code switch
    {
        "terminal.no_backend" => TerminalRefusal.NoBackend,
        "terminal.not_found" => TerminalRefusal.NotFound,
        "terminal.forbidden" => TerminalRefusal.Forbidden,
        "terminal.writer_held" => TerminalRefusal.WriterHeld,
        "terminal.limit_reached" => TerminalRefusal.LimitReached,
        "terminal.invalid_size" => TerminalRefusal.InvalidSize,
        "terminal.rate_limited" => TerminalRefusal.RateLimited,
        _ => null,
    };

    /// <summary>The error frame for a refused result.</summary>
    public static TerminalError ToError(string terminalId, TerminalResult result) =>
        new(terminalId, ErrorCode(result.Refusal), result.Detail ?? result.Refusal.ToString());

    /// <summary>The frame confirming an open or attach. <paramref name="replayFrom"/> is the first replayable sequence.</summary>
    public static TerminalOpened ToOpened(TerminalInfo info, long replayFrom)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new TerminalOpened(info.TerminalId, info.WriterMemberId is not null, replayFrom);
    }

    /// <summary>The output frame of a chunk. The bytes are copied so the ring's buffer is never exposed to a serializer.</summary>
    public static TerminalOutput ToOutput(string terminalId, TerminalOutputChunk chunk) =>
        new(terminalId, chunk.Seq, chunk.Bytes.ToArray());

    /// <summary>Reads an output frame back into a chunk (not a replay).</summary>
    public static TerminalOutputChunk FromOutput(TerminalOutput frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new TerminalOutputChunk(frame.Seq, frame.Bytes ?? [], IsReplay: false);
    }

    /// <summary>The exit frame.</summary>
    public static TerminalExit ToExit(string terminalId, int code) => new(terminalId, code);

    /// <summary>The control reply frame.</summary>
    public static TerminalControl ToControlReply(string terminalId, bool granted) =>
        new(terminalId, granted ? ActionGranted : ActionDenied);

    /// <summary>Handles one client frame that needs no stream state: input, resize, control and close. Other frames return <c>null</c>.</summary>
    /// <returns>The reply frame, or <c>null</c> when the operation needs no reply (an accepted input or resize).</returns>
    public static async Task<TerminalFrame?> HandleAsync(ITerminalBroker broker, TerminalCaller caller, TerminalFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(frame);
        switch (frame)
        {
            case TerminalInput input:
                return ErrorOrNull(input.TerminalId, await broker.WriteAsync(caller, input.TerminalId, input.Bytes ?? [], ct).ConfigureAwait(false));
            case TerminalResize resize:
                return ErrorOrNull(resize.TerminalId, await broker.ResizeAsync(caller, resize.TerminalId, resize.Cols, resize.Rows, ct).ConfigureAwait(false));
            case TerminalClose close:
                return ErrorOrNull(close.TerminalId, await broker.CloseAsync(caller, close.TerminalId, ct).ConfigureAwait(false));
            case TerminalControl control:
                {
                    TerminalResult result;
                    if (string.Equals(control.Action, ActionTake, StringComparison.Ordinal))
                    {
                        result = await broker.TakeControlAsync(caller, control.TerminalId, ct).ConfigureAwait(false);
                    }
                    else if (string.Equals(control.Action, ActionRelease, StringComparison.Ordinal))
                    {
                        result = await broker.ReleaseControlAsync(caller, control.TerminalId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        return new TerminalError(control.TerminalId, "terminal.bad_request", "unknown control action");
                    }

                    return result.Ok ? ToControlReply(control.TerminalId, granted: true) : ToControlReply(control.TerminalId, granted: false);
                }

            default:
                return null;
        }
    }

    private static TerminalError? ErrorOrNull(string terminalId, TerminalResult result) =>
        result.Ok ? null : ToError(terminalId, result);
}
