using Bazaar.Contracts.Kodo;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Maps the Kodo status snapshot (doc 46 section 15) to the read model of the admin ports (doc 51 section 5, doc 58 section 3). One
/// mapping serves the web panel (in the server process) and the resident TUI (a separate process reading <see cref="KodoSnapshotFile"/>),
/// so both show the same words. The state words are the ones the TUI grades: a component's wire colour becomes healthy, degraded or
/// failing, and an unknown reading stays "unknown" so it can never read as green. Only cause codes reach the view, and nothing here
/// carries a path, a payload or a secret.
/// </summary>
public static class KodoAdminViews
{
    /// <summary>How long a snapshot may stand before the reader calls Kodo silent. The silence budget of doc 46 section 15.6 is 30 seconds; a snapshot file adds a tick of latency.</summary>
    public static TimeSpan DefaultStaleAfter { get; } = TimeSpan.FromSeconds(60);

    /// <summary>The check name of the overall state, listed first.</summary>
    public const string OverallCheck = "overall";

    /// <summary>The id of the one incident the view derives when Kodo is in incident mode.</summary>
    public const string IncidentModeId = "incident-mode";

    /// <summary>Builds the view of a snapshot read at <paramref name="now"/>. A snapshot older than <paramref name="staleAfter"/> reads as silent: no score, and an "unobserved" check (doc 46 section 8.3).</summary>
    public static AdminKodoView FromSnapshot(KodoStatusSnapshotDto snapshot, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        KodoStatusDto status = snapshot.Status;
        List<AdminKodoCheck> checks = [];
        TimeSpan age = now - status.AsOf;
        bool silent = age > staleAfter;
        if (silent)
        {
            checks.Add(new AdminKodoCheck(OverallCheck, "unobserved", $"no status for {(long)age.TotalSeconds} seconds"));
        }
        else
        {
            checks.Add(new AdminKodoCheck(OverallCheck, status.State, status.UnderlyingState is { Length: > 0 } under ? "underlying " + under : null));
        }

        foreach (KodoComponentDto component in snapshot.Components)
        {
            checks.Add(new AdminKodoCheck(component.Id, StateWord(component.State), component.Cause));
        }

        List<AdminKodoIncident> incidents = [];
        if (!silent && string.Equals(status.State, "frozen", StringComparison.OrdinalIgnoreCase))
        {
            // Kodo journals no incident record the read model can name yet (doc 46 section 16), so the one incident derived here is
            // incident mode itself. Its opening time is not on the snapshot; the snapshot time is the latest honest value.
            incidents.Add(new AdminKodoIncident(
                IncidentModeId,
                status.UnderlyingState is { Length: > 0 } u ? $"Incident mode is active (underlying {u})" : "Incident mode is active",
                "critical",
                status.AsOf,
                RunbookLink: null));
        }

        return new AdminKodoView(silent ? null : status.Score, checks, incidents);
    }

    /// <summary>The view while Kodo has not completed its first evaluation.</summary>
    public static AdminKodoView NotReady() =>
        new(null, [new AdminKodoCheck(OverallCheck, "unknown", "Kodo has not completed its first pass")], []);

    /// <summary>The view when the operator switched Kodo off.</summary>
    public static AdminKodoView Disabled() =>
        new(null, [new AdminKodoCheck(OverallCheck, "disabled", "Kodo is switched off on this server")], []);

    /// <summary>A component's wire colour as the severity word the TUI and panel grade.</summary>
    public static string StateWord(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "green" => "healthy",
        "amber" => "degraded",
        "red" => "failing",
        "unknown" => "unknown",
        { Length: > 0 } other => other,
        _ => "unknown",
    };
}
