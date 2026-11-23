namespace Bazaar.Client.Calcinato.Sources;

/// <summary>What a remote terminal source reports besides output (doc 52, section 5.3 status line).</summary>
public enum TerminalStatusKind
{
    /// <summary>The server accepted the attach.</summary>
    Opened,

    /// <summary>The writer role changed (granted or denied).</summary>
    ControlChanged,

    /// <summary>A replay was requested after a gap, an overflow or a resubscribe. Drives the replay marker.</summary>
    ReplayRequested,

    /// <summary>The internal buffer was full and the oldest chunk was dropped.</summary>
    Overflow,

    /// <summary>Input was refused because the source is read-only or has failed closed.</summary>
    WriteRefused,

    /// <summary>The server sent an error. Input is disabled for the rest of the source's life.</summary>
    Error,

    /// <summary>The frame stream closed before the terminal exited.</summary>
    Disconnected,

    /// <summary>The terminal exited.</summary>
    Exited,
}

/// <summary>One status event from a remote terminal source.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Code">Server error code, replay start sequence, or null.</param>
/// <param name="Message">Human-readable detail, or null.</param>
public sealed record TerminalSourceStatus(TerminalStatusKind Kind, string? Code = null, string? Message = null);
