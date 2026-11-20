using Bazaar.LoopGuard.Abstractions;

namespace Bazaar.Server.LoopGuard;

/// <summary>The default <see cref="IPsychWardGate"/>: admits nobody until the Psych Ward is built (doc 26). The trip is still recorded and announced.</summary>
public sealed class NoOpPsychWardGate : IPsychWardGate
{
    /// <inheritdoc />
    public Task AdmitAsync(LoopTrip trip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trip);
        return Task.CompletedTask;
    }
}
