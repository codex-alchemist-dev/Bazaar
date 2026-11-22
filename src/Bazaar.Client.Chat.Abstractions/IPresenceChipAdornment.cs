using Avalonia.Controls;

namespace Bazaar.Client.Chat.Abstractions;

/// <summary>
/// A small control another module adds to the presence chip in a GC row (doc 27, section 4; doc 24 presence). It adds
/// no presence label, since that list is fixed (DECISIONS-LOG round 11, C43), and it carries its own accessible name.
/// </summary>
public interface IPresenceChipAdornment
{
    /// <summary>Stable id, such as <c>interrupt-stack</c>. Registering a second adornment with the same id is refused.</summary>
    string Id { get; }

    /// <summary>Position on the chip: a lower value renders first, next to the dot. Equal values keep registration order.</summary>
    int Order { get; }

    /// <summary>
    /// Builds the control for <paramref name="agentId"/>, or returns null when there is nothing to show for that agent;
    /// the chip then renders no slot. Called on the UI thread, and again after <see cref="Changed"/> fires for the agent.
    /// </summary>
    Control? Create(Guid agentId);

    /// <summary>Raised, with the agent id, when this adornment's state for that agent changes. The chip asks again.</summary>
    event Action<Guid>? Changed;
}
