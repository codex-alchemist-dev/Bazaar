using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Reports;

/// <summary>
/// Where a finished report goes (doc 46 sections 16.1 and 16.7): in-client, email, webhook, a group chat post. Concrete sinks are not in this
/// core. A sink may throw or hang; <see cref="ReportRunner"/> contains both.
/// </summary>
public interface IReportSink
{
    /// <summary>Stable identifier of the sink, used in the journal. An identifier: letters, digits, <c>_ . : -</c>.</summary>
    string Id { get; }

    /// <summary>Delivers one report. Returns true when the sink durably accepted it.</summary>
    ValueTask<bool> PublishAsync(HealthReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Gathers the data for one report from the evaluator, score history, SLO tracker and incident record. The host implements it.
/// For a catch-up slot it must return the data as of that slot, not the data of now.
/// </summary>
public interface IReportInputSource
{
    /// <summary>Returns the inputs for the period that ends at <paramref name="periodEnd"/>.</summary>
    ValueTask<ReportInputs> GetAsync(ReportDefinition definition, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken);
}

/// <summary>
/// Appends a journal record. The record is already hashed and linked by <see cref="JournalChain.Create"/>; the implementation
/// stores it and throws when it cannot.
/// </summary>
public interface IReportJournal
{
    /// <summary>Appends one record.</summary>
    ValueTask AppendAsync(JournalRecord record, CancellationToken cancellationToken);
}
