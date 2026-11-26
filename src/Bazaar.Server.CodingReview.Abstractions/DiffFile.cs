namespace Bazaar.Server.CodingReview;

/// <summary>How a file changed in the PR.</summary>
public enum ReviewChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>One hunk of a file diff. <see cref="Body"/> is the raw hunk text, untrusted.</summary>
public sealed record DiffHunk(int OldStart, int NewStart, UntrustedText Body);

/// <summary>One changed file in the PR, as per-file hunks. The path is untrusted because the author chose it.</summary>
public sealed record DiffFile(UntrustedText Path, ReviewChangeKind Kind, IReadOnlyList<DiffHunk> Hunks);
