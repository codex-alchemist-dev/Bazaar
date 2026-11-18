namespace Bazaar.Kodo.Api;

/// <summary>
/// The status snapshot the read API returns and the <c>kodo</c> stream sends on subscribe (doc 46 sections 12.2 and 15).
/// It is the wire shape: states, causes and statuses are strings from <see cref="KodoWireNames"/>. Every property is always
/// written, so one snapshot has one encoding.
/// </summary>
/// <param name="SchemaVersion">The shape version. <see cref="CurrentSchemaVersion"/> for this build.</param>
/// <param name="Revision">The <c>kodo</c> stream revision this snapshot reflects. Deltas after it carry higher revisions.</param>
/// <param name="GeneratedAt">When the snapshot was built, in UTC (the read API's <c>asOf</c>).</param>
/// <param name="State">The state to show. <c>frozen</c> while incident mode is active.</param>
/// <param name="Underlying">The underlying health state, shown beside <c>frozen</c>.</param>
/// <param name="Score">The 0 to 100 health score (doc 46 section 12.2).</param>
/// <param name="JournalHead">The lowercase hex hash of the journal head, or null while the journal has no records.</param>
/// <param name="Components">The per-component states, sorted by id.</param>
public sealed record KodoStatusSnapshot(
    int SchemaVersion,
    long Revision,
    DateTimeOffset GeneratedAt,
    string State,
    string Underlying,
    int Score,
    string? JournalHead,
    IReadOnlyList<KodoComponentStatus> Components)
{
    /// <summary>The shape version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One component's state in a snapshot. <paramref name="Cause"/> explains a non-green state and is null when green.
/// <paramref name="Since"/> is when the component entered its current state.
/// </summary>
/// <param name="Id">The probe or category id, for example <c>host.disk.free</c>.</param>
/// <param name="State">The wire status from <see cref="KodoWireNames.Probe"/>.</param>
/// <param name="Cause">The cause code, or null when green.</param>
/// <param name="Since">When the component entered its current state, in UTC.</param>
public sealed record KodoComponentStatus(string Id, string State, string? Cause, DateTimeOffset Since);
