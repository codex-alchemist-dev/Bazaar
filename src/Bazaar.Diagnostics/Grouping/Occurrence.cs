using Bazaar.Diagnostics.Fingerprinting;

namespace Bazaar.Diagnostics.Grouping;

/// <summary>
/// One reported failure, as the server sees it after ingestion and redaction. The server recomputes the
/// fingerprint from <see cref="Input"/>; the device hint is only compared.
/// </summary>
/// <param name="Input">The inputs of the server-side fingerprint recompute.</param>
/// <param name="Version">The client version as written in the report, for example <c>0.1.0+a1b2c3d</c>.</param>
/// <param name="DeviceClass">The device class (for example desktop), aggregated into the group.</param>
/// <param name="OsFamily">The OS family (for example windows), aggregated into the group.</param>
/// <param name="DeviceFingerprintHint">The <c>fp1</c> value the device computed, or null when it sent none.</param>
public sealed record Occurrence(
    FingerprintInput Input,
    string Version,
    string DeviceClass,
    string OsFamily,
    string? DeviceFingerprintHint);

/// <summary>The daily filing budget (doc 43 section 8.4, <c>issues_per_day</c>), as the caller counts it for the current UTC day.</summary>
/// <param name="IssuesPerDay">The number of new issues allowed per UTC day.</param>
/// <param name="IssuesFiledToday">The number already filed today, counting filings made earlier in the same batch.</param>
public sealed record FilingPolicy(int IssuesPerDay, int IssuesFiledToday)
{
    /// <summary>True when one more issue may be filed today.</summary>
    public bool HasBudget => IssuesFiledToday < IssuesPerDay;
}

/// <summary>The result of a grouping update: the new state and the actions to carry out.</summary>
/// <param name="State">The group after the update.</param>
/// <param name="Actions">The actions, in the order they were decided.</param>
public sealed record GroupUpdate(GroupState State, IReadOnlyList<GroupAction> Actions);
