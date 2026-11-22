namespace Bazaar.Client.Chat.Abstractions;

/// <summary>
/// Where modules register adornments for the presence chip. Heads and modules receive it through the pane context
/// (<c>context.Get&lt;IPresenceAdornmentRegistry&gt;()</c>), so a module needs no reference to <c>Bazaar.Client.GC</c>.
/// </summary>
public interface IPresenceAdornmentRegistry
{
    /// <summary>The adornments in render order (<see cref="IPresenceChipAdornment.Order"/>, then registration order).</summary>
    IReadOnlyList<IPresenceChipAdornment> Adornments { get; }

    /// <summary>Raised after an adornment is added.</summary>
    event Action? Added;

    /// <summary>Adds an adornment.</summary>
    /// <exception cref="InvalidOperationException">An adornment with the same <see cref="IPresenceChipAdornment.Id"/> is already registered.</exception>
    void Add(IPresenceChipAdornment adornment);
}
