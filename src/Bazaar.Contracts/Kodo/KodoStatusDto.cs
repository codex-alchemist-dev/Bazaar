namespace Bazaar.Contracts.Kodo;

/// <summary>
/// One Kodo status snapshot on the wire (doc 46, sections 8, 12 and 14). The server computes every field; clients render
/// them. <see cref="State"/> and <see cref="UnderlyingState"/> use the lower-case wire spelling of doc 46 section 8.1
/// (<c>healthy</c>, <c>degraded</c>, <c>failing</c>, <c>recovering</c>, <c>frozen</c>). <see cref="UnderlyingState"/> is
/// null unless <see cref="State"/> is <c>frozen</c>.
/// </summary>
public sealed record KodoStatusDto(
    string State,
    string? UnderlyingState,
    int Score,
    DateTimeOffset AsOf,
    IReadOnlyList<KodoControlDto> Controls);

/// <summary>
/// One control the server allows this caller to see. <see cref="Control"/> uses the wire spelling <c>restart</c>,
/// <c>isolate</c>, <c>rollback</c> or <c>incidentExit</c>. <see cref="Enabled"/> and <see cref="DisabledReason"/> are
/// decided by the server, including the incident freeze rule; the client never recomputes them.
/// </summary>
public sealed record KodoControlDto(string Control, bool Enabled, string? DisabledReason);
