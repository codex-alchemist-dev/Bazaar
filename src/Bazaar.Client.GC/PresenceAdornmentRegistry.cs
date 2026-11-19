using Bazaar.Client.GC.Abstractions;

namespace Bazaar.Client.GC;

/// <summary>The registry the GC module hosts. Thread-safe; adornments render in order, ties in registration order.</summary>
public sealed class PresenceAdornmentRegistry : IPresenceAdornmentRegistry
{
    private readonly Lock _gate = new();
    private IReadOnlyList<IPresenceChipAdornment> _ordered = [];

    /// <inheritdoc />
    public event Action? Added;

    /// <inheritdoc />
    public IReadOnlyList<IPresenceChipAdornment> Adornments
    {
        get
        {
            lock (_gate) return _ordered;
        }
    }

    /// <inheritdoc />
    public void Add(IPresenceChipAdornment adornment)
    {
        ArgumentNullException.ThrowIfNull(adornment);
        ArgumentException.ThrowIfNullOrWhiteSpace(adornment.Id);

        lock (_gate)
        {
            if (_ordered.Any(a => string.Equals(a.Id, adornment.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A presence adornment with id '{adornment.Id}' is already registered.");
            }

            // OrderBy is stable, so equal orders keep registration order.
            _ordered = [.. _ordered.Append(adornment).OrderBy(a => a.Order)];
        }

        Added?.Invoke();
    }
}
