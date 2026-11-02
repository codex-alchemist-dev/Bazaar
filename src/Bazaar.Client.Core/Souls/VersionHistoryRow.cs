using System.Globalization;

namespace Bazaar.Client.Core.Souls;

/// <summary>
/// One line of the version history, already in display form. Built by <see cref="SoulProfileViewModel"/>.
/// </summary>
/// <param name="Version">Version number, 1 for the first version.</param>
/// <param name="ParentVersion">The version this one followed; null for version 1.</param>
/// <param name="Author">Who appended the version.</param>
/// <param name="Reason">Why the version was appended.</param>
/// <param name="DecisionRef">The decision record that approved the change, or null.</param>
/// <param name="CreatedAt">When the server inserted the version.</param>
public sealed record VersionHistoryRow(
    int Version,
    int? ParentVersion,
    string Author,
    string Reason,
    string? DecisionRef,
    DateTimeOffset CreatedAt)
{
    /// <summary>Version label, such as "v3".</summary>
    public string VersionLabel => "v" + Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parent label, such as "v2", or "none" for version 1.</summary>
    public string ParentLabel => ParentVersion is int parent
        ? "v" + parent.ToString(CultureInfo.InvariantCulture)
        : "none";

    /// <summary>Decision label: the decision reference, or "none".</summary>
    public string DecisionLabel => string.IsNullOrEmpty(DecisionRef) ? "none" : DecisionRef;

    /// <summary>Creation time in UTC, such as "2026-10-25 05:00 UTC".</summary>
    public string CreatedLabel => CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
