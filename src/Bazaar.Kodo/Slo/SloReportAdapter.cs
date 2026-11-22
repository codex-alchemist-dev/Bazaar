using Bazaar.Kodo.Reports;

namespace Bazaar.Kodo.Slo;

/// <summary>
/// Turns SLO statuses into the report builder's existing <see cref="ReportSlo"/> input (doc 46 sections 12.3 and 16.7). Both figures are
/// rounded down to permille, so a report never overstates a target or the budget left. A status with no data reports no budget left.
/// </summary>
public static class SloReportAdapter
{
    /// <summary>One status as a report line.</summary>
    public static ReportSlo ToReportSlo(SloStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        int remaining = status.Health == SloHealth.NoData ? 0 : status.RemainingBudgetPpm;
        return new ReportSlo(status.Id, status.TargetPpm / 1_000, remaining / 1_000);
    }

    /// <summary>A snapshot as report lines, in the snapshot's id order.</summary>
    public static IReadOnlyList<ReportSlo> ToReportSlos(SloSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. snapshot.Statuses.Select(ToReportSlo)];
    }
}
