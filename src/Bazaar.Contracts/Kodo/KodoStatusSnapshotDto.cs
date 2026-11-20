namespace Bazaar.Contracts.Kodo;

/// <summary>
/// The status snapshot the read API returns and the <c>kodo</c> stream sends on subscribe (doc 46 sections 12.2 and 15). The
/// status itself is <see cref="KodoStatusDto"/>, the one status wire type; this record adds the stream revision, the journal
/// head and the per-component states around it. Its <see cref="Status"/> carries <c>asOf</c>, so a snapshot has no second clock.
/// </summary>
/// <param name="SchemaVersion">The shape version. <see cref="CurrentSchemaVersion"/> for this build.</param>
/// <param name="Revision">The <c>kodo</c> stream revision this snapshot reflects. Deltas after it carry higher revisions.</param>
/// <param name="Status">The status payload. Its <c>AsOf</c> is when the snapshot was built, in UTC.</param>
/// <param name="JournalHead">The lowercase hex hash of the journal head, or null while the journal has no records.</param>
/// <param name="Components">The per-component states, sorted by id.</param>
public sealed record KodoStatusSnapshotDto(
    int SchemaVersion,
    long Revision,
    KodoStatusDto Status,
    string? JournalHead,
    IReadOnlyList<KodoComponentDto> Components)
{
    /// <summary>The shape version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One component's state in a snapshot. <paramref name="Cause"/> explains a non-green state and is null when green.
/// </summary>
/// <param name="Id">The probe or category id, for example <c>host.disk.free</c>.</param>
/// <param name="State">The wire status: <c>green</c>, <c>amber</c>, <c>red</c> or <c>unknown</c>.</param>
/// <param name="Cause">The cause code, or null when green.</param>
/// <param name="Since">When the component entered its current state, in UTC.</param>
public sealed record KodoComponentDto(string Id, string State, string? Cause, DateTimeOffset Since);
