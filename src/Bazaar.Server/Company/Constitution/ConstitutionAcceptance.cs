// TODO(component): move to the Company component project when PR #125 merges.
using System.Text.RegularExpressions;

namespace Bazaar.Server.Company.Constitution;

/// <summary>Why a Constitution commit was not activated.</summary>
public enum ConstitutionRefusalReason
{
    /// <summary>The commit SHA is not 40 lowercase hexadecimal characters.</summary>
    InvalidCommitSha,

    /// <summary>The commit has not been accepted (no owner-approved Council outcome yet).</summary>
    CommitNotAccepted,

    /// <summary>The commit text failed to parse or lacks a required heading.</summary>
    ParseFailed,
}

/// <summary>
/// The activation decision for a Constitution commit. Refusal never changes the active commit:
/// <see cref="PreviousCommitRemainsActive"/> is true whenever <see cref="Activated"/> is false.
/// </summary>
/// <param name="Activated">True when the commit becomes the active Constitution.</param>
/// <param name="CommitSha">The activated commit SHA, or null on refusal.</param>
/// <param name="Reason">Why the commit was refused, or null when activated.</param>
public sealed record ConstitutionActivation(bool Activated, string? CommitSha, ConstitutionRefusalReason? Reason)
{
    /// <summary>True on refusal: the previously active commit keeps running and nothing is half-applied.</summary>
    public bool PreviousCommitRemainsActive => !Activated;

    /// <summary>Builds an activation for a commit that passed every check.</summary>
    /// <param name="commitSha">The validated commit SHA.</param>
    public static ConstitutionActivation Activate(string commitSha) => new(true, commitSha, null);

    /// <summary>Builds a refusal.</summary>
    /// <param name="reason">Why the commit was refused.</param>
    public static ConstitutionActivation Refuse(ConstitutionRefusalReason reason) => new(false, null, reason);
}

/// <summary>
/// Pure acceptance check for Constitution commits (doc 24, section 1). A commit activates only when its
/// SHA is valid, the commit is accepted, and its text parsed. No I/O.
/// </summary>
public static partial class ConstitutionAcceptance
{
    /// <summary>
    /// Decides whether a commit becomes the active Constitution. Checks run in this order: SHA format,
    /// acceptance, then parse result.
    /// </summary>
    /// <param name="parse">The parse result for the commit's Constitution text.</param>
    /// <param name="commitAccepted">True when the commit has been accepted.</param>
    /// <param name="commitSha">The commit SHA, which must be 40 lowercase hexadecimal characters.</param>
    public static ConstitutionActivation Decide(ConstitutionParseResult parse, bool commitAccepted, string? commitSha)
    {
        ArgumentNullException.ThrowIfNull(parse);

        if (commitSha is null || !CommitShaFormat().IsMatch(commitSha))
        {
            return ConstitutionActivation.Refuse(ConstitutionRefusalReason.InvalidCommitSha);
        }

        if (!commitAccepted)
        {
            return ConstitutionActivation.Refuse(ConstitutionRefusalReason.CommitNotAccepted);
        }

        if (!parse.Success)
        {
            return ConstitutionActivation.Refuse(ConstitutionRefusalReason.ParseFailed);
        }

        return ConstitutionActivation.Activate(commitSha);
    }

    /// <summary>Exactly 40 lowercase hexadecimal characters. <c>\z</c> rejects a trailing newline.</summary>
    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaFormat();
}
