namespace Bazaar.Server.Interrupts;

/// <summary>Raised when a stack operation is refused. The stack is unchanged.</summary>
public class InterruptStackException(string message) : Exception(message);

/// <summary>Raised when a third frame is pushed onto an agent's stack (depth is two; doc 24, C35).</summary>
public sealed class InterruptDepthExceededException(Guid agentId)
    : InterruptStackException($"Agent '{agentId}' already has two interrupt frames; a third push is refused.")
{
    /// <summary>The agent whose stack is full.</summary>
    public Guid AgentId { get; } = agentId;
}

/// <summary>Raised when a pop names a frame that is not the active top of the stack.</summary>
public sealed class InterruptFrameNotTopException(Guid frameId)
    : InterruptStackException($"Interrupt frame '{frameId}' is not the active top of its stack and cannot be popped.");
