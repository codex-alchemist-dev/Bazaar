namespace Bazaar.Client.Kodo;

/// <summary>The kinds of journal event the Audit and updates view lists (doc 46, section 14.2, view 8).</summary>
public enum KodoEventKind
{
    /// <summary>An admin or security audit event.</summary>
    Audit,

    /// <summary>An update event (doc 49).</summary>
    Update,
}

/// <summary>
/// One journal event as the server reports it (doc 46, section 15.1, <c>GET /events</c>): its record id, when it happened, its
/// kind, a one-line summary the server has already scrubbed, and its position in the hash chain. The client renders it and
/// decides nothing about it.
/// </summary>
public sealed record KodoEventRow(string RecordId, DateTimeOffset At, KodoEventKind Kind, string Summary, long ChainPosition);
