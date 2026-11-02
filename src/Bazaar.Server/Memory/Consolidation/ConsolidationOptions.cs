using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.Memory.Consolidation;

/// <summary>
/// Per-run settings for consolidation (docs/design/08-memory-system.md, Settings table).
/// <see cref="MaxMinutes"/> is <c>memory.consolidation.max_minutes</c> and <see cref="MaxTokens"/> is
/// <c>memory.consolidation.max_tokens</c>. The shipped defaults are 10 and 50000.
/// </summary>
/// <param name="MaxMinutes">Wall-clock cap for one run, measured on the runner's <see cref="TimeProvider"/>.</param>
/// <param name="MaxTokens">Token cap for one run: input plus output, cache reads included.</param>
/// <param name="Route">Model route name passed to the gateway.</param>
/// <param name="MaxNoteBytes">Note size limit passed to <see cref="SoulGuard"/> (<c>memory.note_max_bytes</c>).</param>
public sealed record ConsolidationOptions(
    int MaxMinutes = 10,
    int MaxTokens = 50000,
    string Route = "consolidation",
    int MaxNoteBytes = SoulGuard.DefaultMaxNoteBytes)
{
    /// <summary>Checks the numeric limits and the route.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMinutes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNoteBytes, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(Route);
    }
}
