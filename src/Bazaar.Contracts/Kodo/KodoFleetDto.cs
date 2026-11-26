namespace Bazaar.Contracts.Kodo;

/// <summary>
/// The Fleet view's snapshot (doc 46 section 14.2 item 7): the status of every server the viewer's client knows, in one
/// payload. The server computes every field; the client renders them and derives nothing but <c>unobserved</c> from age.
/// </summary>
/// <param name="SchemaVersion">The shape version. <see cref="CurrentSchemaVersion"/> for this build.</param>
/// <param name="Revision">The stream revision this snapshot reflects, as for <see cref="KodoStatusSnapshotDto"/>.</param>
/// <param name="AsOf">When the snapshot was built, in UTC.</param>
/// <param name="Servers">One row per server, sorted by <see cref="KodoServerStatusDto.ServerId"/>.</param>
public sealed record KodoFleetSnapshotDto(
    int SchemaVersion,
    long Revision,
    DateTimeOffset AsOf,
    IReadOnlyList<KodoServerStatusDto> Servers)
{
    /// <summary>The shape version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One server's row in the fleet snapshot. <see cref="State"/> and <see cref="UnderlyingState"/> use the wire spelling of
/// doc 46 section 8.1, as in <see cref="KodoStatusDto"/>; <see cref="UnderlyingState"/> is null unless the state is
/// <c>frozen</c>.
/// </summary>
/// <param name="ServerId">The stable server id.</param>
/// <param name="DisplayName">The operator's name for the server.</param>
/// <param name="State">The wire state.</param>
/// <param name="UnderlyingState">The underlying state, set only when <paramref name="State"/> is <c>frozen</c>.</param>
/// <param name="Score">The 0 to 100 health score.</param>
/// <param name="AsOf">When this row's status was computed, in UTC. A client marks the row unobserved when it is too old.</param>
/// <param name="Version">The server's running version, or null when not reported.</param>
/// <param name="OpenAlerts">The number of open alerts.</param>
/// <param name="IncidentOpen">True while incident mode is active on this server.</param>
public sealed record KodoServerStatusDto(
    string ServerId,
    string DisplayName,
    string State,
    string? UnderlyingState,
    int Score,
    DateTimeOffset AsOf,
    string? Version,
    int OpenAlerts,
    bool IncidentOpen);
