namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// The motion a ballot prompt shows (docs/design/10-council-and-voting.md, Lifecycle step 1).
/// </summary>
/// <param name="Subject">The motion text. Must not name an eligible voter by id (the privacy guard denies it).</param>
/// <param name="Kind">Ballot shape. Approve/reject takes no options; option choice takes 2 to 6 distinct options.</param>
/// <param name="MotionClass">Ordinary or bigger change; a bigger change passes only when unanimous.</param>
/// <param name="Options">Option choices for <see cref="VoteKind.OptionChoice"/>; empty for approve/reject.</param>
public sealed record CouncilMotion(
    string Subject,
    VoteKind Kind,
    MotionClass MotionClass,
    IReadOnlyList<string> Options);
