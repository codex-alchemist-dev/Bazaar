namespace Bazaar.Client.TimeMachine;

/// <summary>
/// One part of a replayed frame that another module owns, as the client shows it. Mirrors the server's replay part
/// (doc 26 section 4) without referencing it: a missing or failing module is shown as unavailable, never as empty.
/// </summary>
/// <param name="Name">Heading, for example "Kanban".</param>
/// <param name="IsAvailable">False when the server could not rebuild the part.</param>
/// <param name="Summary">One line describing the part as it stood, or null when unavailable.</param>
/// <param name="Reason">Why the part is unavailable, or null when available.</param>
public sealed record ReplayPartView(string Name, bool IsAvailable, string? Summary, string? Reason)
{
    public static ReplayPartView Available(string name, string summary) => new(name, true, summary, null);

    public static ReplayPartView Unavailable(string name, string reason) => new(name, false, null, reason);

    /// <summary>The line the view shows.</summary>
    public string Line => IsAvailable ? $"{Name}: {Summary}" : $"{Name}: unavailable ({Reason})";
}

/// <summary>One message as it stood at the viewed seq (latest version at or below it).</summary>
/// <param name="MessageId">Stable message identity.</param>
/// <param name="AuthorLabel">Display name of the writer of the version shown.</param>
/// <param name="VersionSeq">Seq of the event that created the version shown.</param>
/// <param name="Version">Version number shown.</param>
/// <param name="Edited">True when the version shown is not the first.</param>
/// <param name="Quarantined">True when the version shown was quarantined by then.</param>
/// <param name="Text">Decoded text of the version shown, or null when the payload has no text.</param>
public sealed record ReplayMessageView(
    Guid MessageId,
    string AuthorLabel,
    long VersionSeq,
    int Version,
    bool Edited,
    bool Quarantined,
    string? Text)
{
    /// <summary>The line the view shows: author, text, then the markers.</summary>
    public string Line =>
        $"{AuthorLabel}: {Text ?? "(no text)"}" + (Edited ? " (edited)" : string.Empty) + (Quarantined ? " [quarantined]" : string.Empty);
}

/// <summary>A GC rendered at a point in its history, as the scrubber shows it. Read-only.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="EffectiveSeq">The seq actually rendered. The server may clamp the asked-for seq to the viewer's grant.</param>
/// <param name="HeadSeq">The newest seq the viewer may scrub to.</param>
/// <param name="Clamped">True when <paramref name="EffectiveSeq"/> is below the seq asked for.</param>
/// <param name="Messages">Messages then, in first-seq order.</param>
/// <param name="Parts">Kanban, Vault, Souls and Reputation then, each available or unavailable.</param>
public sealed record ReplayFrameView(
    Guid GcId,
    long EffectiveSeq,
    long HeadSeq,
    bool Clamped,
    IReadOnlyList<ReplayMessageView> Messages,
    IReadOnlyList<ReplayPartView> Parts);

/// <summary>A request to fork a project GC from a point of a source GC. The request id makes a repeat idempotent.</summary>
public sealed record ReplayForkRequest(Guid RequestId, Guid SourceGcId, long SourceSeq);

/// <summary>The project GC a fork made. No messages are copied into it by default (doc 26 section 4, Q1).</summary>
/// <param name="NewGcId">The new project GC.</param>
/// <param name="SourceSeq">The seq forked at after the server's clamp.</param>
/// <param name="Created">False when the server returned an earlier fork for the same request.</param>
public sealed record ReplayForkOutcome(Guid NewGcId, long SourceSeq, bool Created);

/// <summary>The server refused or could not serve a replay call. The message is safe to show.</summary>
public sealed class ReplayClientException : Exception
{
    public ReplayClientException(string message)
        : base(message)
    {
    }
}
