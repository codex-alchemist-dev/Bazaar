namespace Bazaar.Contracts.Kodo;

/// <summary>
/// One displayed-state change in the state history (doc 46 sections 7.10 and 15.1). Carries the journal sequence, the time and the
/// two wire state names only, never a payload.
/// </summary>
/// <param name="Sequence">The journal record that carries the change.</param>
/// <param name="At">When it was recorded, in UTC.</param>
/// <param name="From">Wire name of the previous state.</param>
/// <param name="To">Wire name of the new state.</param>
public sealed record KodoStateHistoryEntryDto(long Sequence, DateTimeOffset At, string From, string To);

/// <summary>
/// One page of <c>GET /api/kodo/v1/history</c>: state changes newest first. <see cref="NextCursor"/> is set when more may follow;
/// pass it back as <c>cursor</c> to read the next page.
/// </summary>
/// <param name="Schema">Contract version, 1.</param>
/// <param name="Entries">The changes, newest first.</param>
/// <param name="NextCursor">The cursor for the next page, or null when this page was the last.</param>
public sealed record KodoStateHistoryDto(int Schema, IReadOnlyList<KodoStateHistoryEntryDto> Entries, string? NextCursor);
