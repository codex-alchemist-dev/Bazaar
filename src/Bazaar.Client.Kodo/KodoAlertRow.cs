namespace Bazaar.Client.Kodo;

/// <summary>An alert's severity as the client shows it (doc 46, section 16.1). It orders: Critical is the highest.</summary>
public enum KodoAlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// One open alert as the server reports it (doc 46, section 15.1, <c>GET /alerts</c>). <paramref name="Fingerprint"/> names the
/// alert and is what acknowledgement sends back. <paramref name="Rule"/> is the rule that fired, always shown so the reason is
/// visible (section 13.4). <paramref name="CanAcknowledge"/> is the server's answer for this viewer's role; the client never
/// works it out.
/// </summary>
public sealed record KodoAlertRow(
    string Fingerprint,
    KodoAlertSeverity Severity,
    string Rule,
    DateTimeOffset RaisedAt,
    bool CanAcknowledge,
    bool Acknowledged);
