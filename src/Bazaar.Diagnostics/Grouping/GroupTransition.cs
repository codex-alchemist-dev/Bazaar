using Bazaar.Diagnostics.Fingerprinting;

namespace Bazaar.Diagnostics.Grouping;

/// <summary>
/// The pure grouping update (doc 43 sections 8.3 and 8.4). It takes the group as stored (or null for a new group),
/// one occurrence, the filing budget and the time, and returns the new group and the effects the caller must carry
/// out. It reads no clock, store or network, so the same inputs always give the same result.
/// </summary>
public static class GroupTransition
{
    /// <summary>The occurrence counts at which a comment is added to the issue (doc 43 section 8.4).</summary>
    public static IReadOnlyList<long> CommentThresholds { get; } = [1, 10, 100, 1000];

    /// <summary>Applies one occurrence to a group.</summary>
    /// <param name="existing">The stored group, or null when no group has this fingerprint yet.</param>
    /// <param name="occurrence">The occurrence, after ingestion and redaction.</param>
    /// <param name="policy">The daily filing budget as the caller counts it.</param>
    /// <param name="now">The current time, from the caller's clock.</param>
    /// <returns>The new group state and the actions, in the order they were decided.</returns>
    public static GroupUpdate Apply(GroupState? existing, Occurrence occurrence, FilingPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(policy);

        var actions = new List<GroupAction>();
        var serverFingerprint = Fingerprint.Compute(occurrence.Input);
        if (occurrence.DeviceFingerprintHint is { } hint && !string.Equals(hint, serverFingerprint, StringComparison.Ordinal))
        {
            actions.Add(new RecordMismatch(hint, serverFingerprint));
        }

        if (existing is not null && !string.Equals(existing.Fingerprint, serverFingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException("The stored group does not belong to this occurrence's fingerprint.", nameof(existing));
        }

        var state = Absorb(existing ?? GroupState.Create(serverFingerprint, occurrence.Version, now), existing is null, occurrence, now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (state.IssueNumber is null)
        {
            if (policy.HasBudget)
            {
                state = state with { Status = GroupStatus.Open };
                actions.Add(new FileIssue(serverFingerprint));
            }
            else
            {
                state = state with { Status = GroupStatus.FilingDeferred };
                actions.Add(new QueueDeferred(serverFingerprint));
            }
        }
        else if (state.IssueClosed)
        {
            if (IsRegression(state, occurrence.Version))
            {
                var fixedIn = state.FixedInVersion!;
                state = state with { IssueClosed = false, Status = GroupStatus.ResolvedIn, LastCommentDate = today };
                actions.Add(new Reopen(fixedIn, occurrence.Version, $"Seen again in {occurrence.Version}, which is at or above the version this was fixed in ({fixedIn}). Reopening."));
            }
        }
        else if (CommentThresholds.Contains(state.OccurrenceCount) && state.LastCommentDate != today)
        {
            state = state with { LastCommentDate = today };
            actions.Add(new AddComment(CommentReason.OccurrenceThreshold, state.OccurrenceCount, CommentText(state)));
        }

        return new GroupUpdate(state, actions);
    }

    private static GroupState Absorb(GroupState state, bool isNew, Occurrence occurrence, DateTimeOffset now)
    {
        var first = state.FirstSeenVersion;
        var last = state.LastSeenVersion;
        if (!isNew && ReleaseVersion.TryParse(occurrence.Version, out var seen))
        {
            if (!ReleaseVersion.TryParse(first, out var low) || seen < low)
            {
                first = occurrence.Version;
            }

            if (!ReleaseVersion.TryParse(last, out var high) || seen > high)
            {
                last = occurrence.Version;
            }
        }

        return state with
        {
            OccurrenceCount = state.OccurrenceCount + 1,
            FirstSeenVersion = first,
            LastSeenVersion = last,
            LastSeenAt = now,
            DeviceClasses = state.DeviceClasses.Add(occurrence.DeviceClass),
            OsFamilies = state.OsFamilies.Add(occurrence.OsFamily),
        };
    }

    private static bool IsRegression(GroupState state, string occurrenceVersion) =>
        state.FixedInVersion is { } fixedIn
        && ReleaseVersion.TryParse(fixedIn, out var fixedVersion)
        && ReleaseVersion.TryParse(occurrenceVersion, out var seen)
        && seen >= fixedVersion;

    private static string CommentText(GroupState state) =>
        $"Seen {state.OccurrenceCount} times. Versions {state.FirstSeenVersion} to {state.LastSeenVersion}. "
        + $"Device classes: {string.Join(", ", state.DeviceClasses)}. OS families: {string.Join(", ", state.OsFamilies)}.";
}
