namespace Bazaar.Diagnostics.Grouping;

/// <summary>An effect the caller must carry out (GitHub write, queue entry, diagnostics) after a grouping update.</summary>
public abstract record GroupAction;

/// <summary>Create the group's issue now. The caller records the number with <see cref="GroupState.WithIssueNumber"/> and counts one filing against the day's budget.</summary>
/// <param name="Fingerprint">The group key, which goes in the issue body marker.</param>
public sealed record FileIssue(string Fingerprint) : GroupAction;

/// <summary>The group needs an issue, but the daily filing budget is used up. It waits in the queue.</summary>
/// <param name="Fingerprint">The group key.</param>
public sealed record QueueDeferred(string Fingerprint) : GroupAction;

/// <summary>Add a comment to the group's issue.</summary>
/// <param name="Reason">Why the comment is made.</param>
/// <param name="OccurrenceCount">The count the comment reports.</param>
/// <param name="Text">The comment text. It carries counts and versions, never a stack.</param>
public sealed record AddComment(CommentReason Reason, long OccurrenceCount, string Text) : GroupAction;

/// <summary>Reopen the closed issue because an occurrence arrived at or above the version it was fixed in.</summary>
/// <param name="FixedInVersion">The version the issue was closed as fixed in.</param>
/// <param name="OccurrenceVersion">The version the regression was seen in.</param>
/// <param name="Text">The reopen comment, which names both versions.</param>
public sealed record Reopen(string FixedInVersion, string OccurrenceVersion, string Text) : GroupAction;

/// <summary>A device fingerprint hint differs from the server recompute. The server value is used. This points at a normalisation bug.</summary>
/// <param name="DeviceFingerprint">The hint the device sent.</param>
/// <param name="ServerFingerprint">The fingerprint the server computed and used.</param>
public sealed record RecordMismatch(string DeviceFingerprint, string ServerFingerprint) : GroupAction;

/// <summary>Why a comment is added.</summary>
public enum CommentReason
{
    /// <summary>The occurrence count reached a threshold (1, 10, 100 or 1000).</summary>
    OccurrenceThreshold,
}
