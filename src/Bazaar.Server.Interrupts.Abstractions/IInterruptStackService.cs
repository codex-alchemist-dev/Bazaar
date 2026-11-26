namespace Bazaar.Server.Interrupts;

/// <summary>
/// The per-agent interrupt stack (doc 24 section 3). Push suspends the current top and makes the new frame active. Pop ends
/// the top and resumes the frame beneath it. Only the active frame runs; the runtime (doc 06) acts on the result.
/// </summary>
public interface IInterruptStackService
{
    /// <summary>
    /// Pushes a frame. Refused with <see cref="InterruptDepthExceededException"/> when two frames are already live. An
    /// identical system push (same source, reason and GC) within 60 seconds of a live frame returns that frame instead of
    /// adding one. The reason is sealed under <paramref name="authorId"/>'s person key; for a system source the author is ignored
    /// and the company owner's key is used (doc 24 section 5, "Interrupt reason sealing"). <paramref name="emergency"/> is stored
    /// only for an owner source; a system frame always stores false.
    /// </summary>
    Task<InterruptFrame> PushAsync(
        Guid agentId, Guid authorId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt = null, bool emergency = false,
        CancellationToken ct = default);

    /// <summary>
    /// Ends the active top frame and resumes its parent, returning the resumed frame, or null when the stack is then empty.
    /// Idempotent: a frame that is already done or cancelled returns null and changes nothing. Throws
    /// <see cref="InterruptFrameNotTopException"/> for a suspended frame.
    /// </summary>
    Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct = default);

    /// <summary>The live frames (active and suspended) of one agent, top first. Empty when the agent has none.</summary>
    Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct = default);
}
