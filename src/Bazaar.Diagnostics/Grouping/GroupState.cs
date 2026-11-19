using System.Collections.Immutable;

namespace Bazaar.Diagnostics.Grouping;

/// <summary>
/// The server's view of one crash group (doc 43 section 8.4). It holds no personal data: counts, versions, the
/// aggregated device classes and OS families, and the issue reference. It is immutable; every change returns a
/// new value.
/// </summary>
public sealed record GroupState
{
    /// <summary>The server-computed <c>fp1</c> fingerprint that keys the group.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Occurrences seen, including the one that created the group.</summary>
    public long OccurrenceCount { get; init; }

    /// <summary>The lowest version seen, by SemVer precedence, as written by the device.</summary>
    public required string FirstSeenVersion { get; init; }

    /// <summary>The highest version seen, by SemVer precedence, as written by the device.</summary>
    public required string LastSeenVersion { get; init; }

    /// <summary>When the first occurrence was seen (from the injected clock).</summary>
    public required DateTimeOffset FirstSeenAt { get; init; }

    /// <summary>When the most recent occurrence was seen (from the injected clock).</summary>
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>Device classes seen across all occurrences.</summary>
    public ImmutableSortedSet<string> DeviceClasses { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    /// <summary>OS families seen across all occurrences.</summary>
    public ImmutableSortedSet<string> OsFamilies { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    /// <summary>The status on the issue side.</summary>
    public GroupStatus Status { get; init; } = GroupStatus.Open;

    /// <summary>The GitHub issue number, or null when no issue exists (never filed, deferred, or deleted by hand).</summary>
    public int? IssueNumber { get; init; }

    /// <summary>True when the issue is known to be closed.</summary>
    public bool IssueClosed { get; init; }

    /// <summary>The version the issue was closed as fixed in, or null when it was closed without one.</summary>
    public string? FixedInVersion { get; init; }

    /// <summary>The UTC calendar day of the last comment or reopen, used for the one-comment-per-day rule.</summary>
    public DateOnly? LastCommentDate { get; init; }

    /// <summary>Creates the state of a group before its first occurrence is applied.</summary>
    public static GroupState Create(string fingerprint, string version, DateTimeOffset now) => new()
    {
        Fingerprint = fingerprint,
        FirstSeenVersion = version,
        LastSeenVersion = version,
        FirstSeenAt = now,
        LastSeenAt = now,
    };

    /// <summary>Records that the GitHub issue was created with this number. Call after a <see cref="FileIssue"/> action.</summary>
    public GroupState WithIssueNumber(int number) => this with { IssueNumber = number, IssueClosed = false };

    /// <summary>
    /// Records that the issue was closed. Pass the version it was fixed in, or null when it was closed without a
    /// fixed version (then a later occurrence never reopens it).
    /// </summary>
    public GroupState WithIssueClosed(string? fixedInVersion)
    {
        if (fixedInVersion is not null)
        {
            _ = ReleaseVersion.Parse(fixedInVersion);
        }

        return this with { IssueClosed = true, FixedInVersion = fixedInVersion };
    }

    /// <summary>
    /// Records that the issue was deleted by hand. The next occurrence files a new issue, or defers the filing if
    /// the budget is used up.
    /// </summary>
    public GroupState WithIssueDeleted() => this with { IssueNumber = null, IssueClosed = false, FixedInVersion = null };
}
