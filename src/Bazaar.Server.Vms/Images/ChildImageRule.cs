namespace Bazaar.Server.Vms.Images;

/// <summary>Outcome of checking a child's parent. <see cref="Ok"/> means the child may be created or started.</summary>
public enum ParentCheckStatus
{
    Ok,
    ParentMissing,
    ParentHashMismatch,
}

/// <summary>A parent check outcome with a human-readable message.</summary>
public sealed record ParentCheckResult(ParentCheckStatus Status, string Message)
{
    public bool IsOk => Status == ParentCheckStatus.Ok;
}

/// <summary>
/// Child-to-parent rules (docs/design/16 "Golden image and rebase"). A child records its parent's SHA-256; the provider
/// refuses a missing parent or one whose hash differs. A parent is kept until its last child retires.
/// </summary>
public static class ChildImageRule
{
    /// <summary>
    /// Checks a child's recorded parent hash against the parent on disk. <paramref name="parentActualSha256"/> is required
    /// only when the parent exists.
    /// </summary>
    public static ParentCheckResult CheckParent(string childRecordedParentSha256, string? parentActualSha256, bool parentExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childRecordedParentSha256);

        if (!parentExists)
            return new ParentCheckResult(ParentCheckStatus.ParentMissing, "The child's parent image is missing; the child is refused.");

        ArgumentException.ThrowIfNullOrWhiteSpace(parentActualSha256);
        if (!string.Equals(childRecordedParentSha256, parentActualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ParentCheckResult(
                ParentCheckStatus.ParentHashMismatch,
                $"The child's parent hash {childRecordedParentSha256} does not match the parent's {parentActualSha256}; the child is refused.");
        }

        return new ParentCheckResult(ParentCheckStatus.Ok, "The child's parent matches.");
    }

    /// <summary>A parent may be retired only when no live child still depends on it.</summary>
    public static bool CanRetireParent(int liveChildCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveChildCount);

        return liveChildCount == 0;
    }
}
