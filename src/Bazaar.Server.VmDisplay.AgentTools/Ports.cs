using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools;

/// <summary>Which of an agent's own displays a tool call addresses.</summary>
public enum AgentDisplayKind
{
    /// <summary>The agent's Linux VM.</summary>
    Linux,

    /// <summary>The agent's Windows VM.</summary>
    Windows,

    /// <summary>The agent's browser session.</summary>
    Browser,
}

/// <summary>
/// Maps a calling agent to its own display targets. The tools take the agent id from the dispatcher's call context and never from
/// the model's arguments, so an agent can only ever address its own VMs and browser session. The host supplies the implementation
/// (it knows the VM registry and the browser sessions); the module offers no tools until one is registered.
/// </summary>
public interface IAgentDisplayTargets
{
    /// <summary>The agent's target of this kind, or <see langword="null"/> when the agent has none (no such VM, or no browser session).</summary>
    DisplayTarget? Resolve(Guid agentId, AgentDisplayKind kind);
}

/// <summary>
/// Optional port: the native pixel size of a display. The display contracts do not expose it (a captured frame may have been
/// downscaled), so a host that knows it registers this and <c>screen.input</c> scales pointer coordinates from the frame the agent
/// looked at to the native size. Without it, coordinates pass through unchanged.
/// </summary>
public interface IDisplayNativeSize
{
    /// <summary>The native size of the target's display, or <see langword="null"/> when unknown.</summary>
    (int Width, int Height)? TryGet(DisplayTarget target);
}

/// <summary>Adapts the display module's <see cref="IDisplayNativeSizeReader"/> to the tools' <see cref="IDisplayNativeSize"/> port.</summary>
internal sealed class ReaderNativeSize(IDisplayNativeSizeReader reader) : IDisplayNativeSize
{
    public (int Width, int Height)? TryGet(DisplayTarget target) => reader.TryGetNativeSize(target);
}
