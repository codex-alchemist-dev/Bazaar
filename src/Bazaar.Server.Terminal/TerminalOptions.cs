namespace Bazaar.Server.Terminal;

/// <summary>
/// Operator settings for the terminal module (doc 52, section 4). Every value has a safe default. <see cref="Normalize"/> clamps
/// each one into its allowed range: a setting may tighten a limit but never loosen it below the floor, and a nonsense value
/// (zero, negative) falls back to the safe bound instead of disabling the protection.
/// </summary>
public sealed record TerminalOptions
{
    /// <summary>Hard floor for the scrollback ring (64 KiB). Settings below this are raised to it.</summary>
    public const int ScrollbackFloorBytes = 64 * 1024;

    /// <summary>Largest allowed terminal dimension (columns or rows).</summary>
    public const int MaxDimension = 1000;

    /// <summary>Scrollback ring bytes per session. Default 1 MiB, floor 64 KiB.</summary>
    public int ScrollbackBytes { get; init; } = 1024 * 1024;

    /// <summary>Bounded frames queued per subscriber before it is dropped to replay. Default 256, range 8 to 4096.</summary>
    public int SubscriberQueueFrames { get; init; } = 256;

    /// <summary>Maximum live sessions per agent. Default 4, minimum 1.</summary>
    public int MaxSessionsPerAgent { get; init; } = 4;

    /// <summary>Maximum live sessions in the company. Default 64, minimum 1.</summary>
    public int MaxSessionsPerCompany { get; init; } = 64;

    /// <summary>Close a session with no input and no output for this long. Default 60 minutes, minimum 1 minute (idle close cannot be switched off).</summary>
    public TimeSpan IdleClose { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Largest payload of one output or guest-input frame. Default 16 KiB, range 256 bytes to 64 KiB, never above the ring size.</summary>
    public int MaxFramePayloadBytes { get; init; } = 16 * 1024;

    /// <summary>Input rate per client, which is also the burst size (a one second bucket). Default 64 KiB/s, minimum 1 KiB/s.</summary>
    public int InputBytesPerSecond { get; init; } = 64 * 1024;

    /// <summary>How long an ended session stays visible so late viewers see the exit. Default 5 seconds, range 0 to 1 minute.</summary>
    public TimeSpan ExitLinger { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the idle and linger sweep runs. Default 1 minute, range 1 second to 10 minutes.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Audit every human input write instead of the first one of each control stint. Default false.</summary>
    public bool AuditEveryHumanInput { get; init; }

    /// <summary>Returns a copy with every value clamped into its allowed range.</summary>
    public TerminalOptions Normalize()
    {
        var scrollback = Math.Clamp(ScrollbackBytes, ScrollbackFloorBytes, 256 * 1024 * 1024);
        var payload = Math.Clamp(MaxFramePayloadBytes, 256, 64 * 1024);
        payload = Math.Min(payload, scrollback);
        return this with
        {
            ScrollbackBytes = scrollback,
            SubscriberQueueFrames = Math.Clamp(SubscriberQueueFrames, 8, 4096),
            MaxSessionsPerAgent = Math.Max(1, MaxSessionsPerAgent),
            MaxSessionsPerCompany = Math.Max(1, MaxSessionsPerCompany),
            IdleClose = IdleClose < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : IdleClose,
            MaxFramePayloadBytes = payload,
            InputBytesPerSecond = Math.Max(1024, InputBytesPerSecond),
            ExitLinger = ExitLinger < TimeSpan.Zero ? TimeSpan.Zero : (ExitLinger > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : ExitLinger),
            SweepInterval = SweepInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : (SweepInterval > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : SweepInterval),
        };
    }
}
