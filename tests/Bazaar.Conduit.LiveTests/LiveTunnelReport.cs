using System.Globalization;
using System.Text;

namespace Bazaar.Conduit.LiveTests;

/// <summary>Outcome of one live tunnel run: what was tried, how long each stage took and the round-trip latency.</summary>
public sealed record LiveTunnelReport(
    string Provider,
    string BinaryVersion,
    int TunnelAttempts,
    TimeSpan TimeToOffer,
    TimeSpan TimeToFirstEcho,
    int ConnectAttempts,
    int Sent,
    int Echoed,
    LatencyStats? Latency,
    bool TerminatesTls)
{
    /// <summary>Renders the report as Markdown, ready to paste into a result doc. The tunnel host is never included.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Provider | {Provider} ({BinaryVersion}) |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Tunnel start attempts | {TunnelAttempts} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Time to published offer | {TimeToOffer.TotalSeconds:F1} s |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| WebSocket connect attempts | {ConnectAttempts} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Time to first echo | {TimeToFirstEcho.TotalSeconds:F1} s |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Echoes | {Echoed} of {Sent} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Offer marked TerminatesTls | {TerminatesTls} |");
        if (Latency is { } l)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Round trip min / p50 / p95 / max | {l.MinMs:F0} / {l.P50Ms:F0} / {l.P95Ms:F0} / {l.MaxMs:F0} ms |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Round trip mean | {l.MeanMs:F0} ms |");
        }

        return sb.ToString();
    }
}
